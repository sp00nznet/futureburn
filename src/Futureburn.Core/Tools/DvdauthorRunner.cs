using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Futureburn.Core.Tools;

// Wrapper for invoking dvdauthor.exe to produce real spec-compliant
// DVD-Video IFO/BUP files (and split VOBs) from an MPEG-PS input.
//
// Usage:
//   var d = DvdauthorRunner.LocateOrThrow();
//   d.Author(new DvdTitleSpec(mpeg, ChapterStarts: chapters, AudioLangs: ...),
//            outputFolder, onLog: line => ...);
//
// Output is laid down at outputFolder/VIDEO_TS/ + outputFolder/AUDIO_TS/.
// dvdauthor handles the entire IFO authoring (TT_SRPT, VTS_PGCI, VTS_C_ADT,
// VTS_VOBU_ADMAP scanning the VOB for NAV packets — all of it) and splits the
// program stream into the 1 GB VOB files DVD-Video requires.

[SupportedOSPlatform("windows")]
public sealed class DvdauthorRunner
{
    public string ExePath { get; }
    public string VersionLine { get; }

    private DvdauthorRunner(string exePath, string versionLine)
    {
        ExePath     = exePath;
        VersionLine = versionLine;
    }

    public static DvdauthorRunner? Locate()
    {
        var info = DvdauthorLocator.Locate();
        return info is null ? null : new DvdauthorRunner(info.Path, info.VersionLine);
    }

    public static DvdauthorRunner LocateOrThrow()
    {
        return Locate() ?? throw new InvalidOperationException(
            "dvdauthor isn't installed on this system. The easiest way to get it on\n" +
            "Windows is DVDStyler, which bundles dvdauthor.exe + spumux.exe:\n" +
            "  winget install AlexThuering.DVDStyler\n" +
            "Then run `futureburn dvdauthor` to verify.");
    }

    /// <summary>
    /// Describes one DVD-Video title to author. The MPEG-PS must already
    /// contain the video and every audio stream; subtitles are expected to
    /// have been muxed in with spumux beforehand.
    /// </summary>
    public sealed record DvdTitleSpec(
        string MpegFile,
        bool IsPal = false,
        string AspectRatio = "4:3",
        // Chapter start times. A leading 00:00:00 is added if absent.
        IReadOnlyList<TimeSpan>? ChapterStarts = null,
        // Two-letter language code per audio stream, in stream order.
        IReadOnlyList<string>? AudioLangs = null,
        // Two-letter language code per subpicture (subtitle) stream.
        IReadOnlyList<string>? SubpictureLangs = null);

    /// <summary>
    /// Author a DVD-Video folder from a single auto-playing title (no menus).
    /// </summary>
    public void Author(DvdTitleSpec spec, string outputFolder, Action<string>? onLog = null)
        => RunXml(BuildXml(spec), outputFolder, onLog);

    /// <summary>
    /// Author a DVD-Video folder with a navigable menu system described by
    /// <paramref name="spec"/> — a root menu and optional scene-selection menu.
    /// </summary>
    public void AuthorWithMenus(MenuDvdSpec spec, string outputFolder, Action<string>? onLog = null)
        => RunXml(BuildMenuXml(spec), outputFolder, onLog);

    // Write the XML to a temp file and run `dvdauthor -o outFolder -x xml`.
    private void RunXml(string xml, string outputFolder, Action<string>? onLog)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"futureburn-dvdauthor-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, xml);
        try
        {
            // -o overrides any "dest" in the XML, so we leave dest out.
            var psi = new ProcessStartInfo
            {
                FileName               = ExePath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(outputFolder);
            psi.ArgumentList.Add("-x"); psi.ArgumentList.Add(xmlPath);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Couldn't start {ExePath}");

            // dvdauthor logs to both stdout and stderr.
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new InvalidOperationException($"dvdauthor exited with code {p.ExitCode}.");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>Back-compat shim: author a plain single title, no chapters/extra streams.</summary>
    public void AuthorSingleTitle(
        string mpegInputPath, string outputFolder,
        bool isPal = false, string aspectRatio = "4:3", Action<string>? onLog = null)
        => Author(new DvdTitleSpec(mpegInputPath, isPal, aspectRatio), outputFolder, onLog);

    /// <summary>
    /// Build the dvdauthor XML control file for one title.
    /// <para>
    /// A &lt;vmgm&gt; section is required so dvdauthor runs its second pass and
    /// produces VIDEO_TS.IFO/BUP. Its single PGC just `jump title 1;`s, so the
    /// disc auto-plays the title on insert — the behavior standalone players
    /// expect. jumppad="yes" inserts the connector cells some players want.
    /// </para>
    /// </summary>
    public static string BuildXml(DvdTitleSpec spec)
    {
        string fmt    = spec.IsPal ? "pal" : "ntsc";
        string aspect = spec.AspectRatio;
        var audio = spec.AudioLangs       ?? Array.Empty<string>();
        var subs  = spec.SubpictureLangs  ?? Array.Empty<string>();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<dvdauthor jumppad=\"yes\">");
        sb.AppendLine("  <vmgm>");
        sb.AppendLine("    <menus>");
        sb.AppendLine($"      <video format=\"{fmt}\" aspect=\"{aspect}\"/>");
        sb.AppendLine("      <pgc entry=\"title\">");
        sb.AppendLine("        <pre>jump title 1;</pre>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </menus>");
        sb.AppendLine("  </vmgm>");
        sb.AppendLine("  <titleset>");
        sb.AppendLine("    <titles>");
        sb.AppendLine($"      <video format=\"{fmt}\" aspect=\"{aspect}\"/>");
        // Audio / subpicture stream declarations must be in the same order as
        // the streams sit in the MPEG-PS, so players label languages right.
        foreach (var lang in audio)
            sb.AppendLine($"      <audio lang=\"{XmlEscape(lang)}\"/>");
        foreach (var lang in subs)
            sb.AppendLine($"      <subpicture lang=\"{XmlEscape(lang)}\"/>");
        sb.AppendLine("      <pgc>");
        var chapters = FormatChapters(spec.ChapterStarts);
        if (chapters.Length > 0)
            sb.AppendLine($"        <vob file=\"{XmlEscape(spec.MpegFile)}\" chapters=\"{chapters}\"/>");
        else
            sb.AppendLine($"        <vob file=\"{XmlEscape(spec.MpegFile)}\"/>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </titles>");
        sb.AppendLine("  </titleset>");
        sb.AppendLine("</dvdauthor>");
        return sb.ToString();
    }

    /// <summary>Kept for callers of the old API.</summary>
    public static string BuildSingleTitleXml(
        string outputFolder, string mpegInput, bool isPal = false, string aspectRatio = "4:3")
        => BuildXml(new DvdTitleSpec(mpegInput, isPal, aspectRatio));

    /// <summary>One menu PGC: its background MPEG and its named buttons.</summary>
    public sealed record MenuPgc(
        string MpegFile,
        // Each button: its name (must match the spumux subpicture button name)
        // and the dvdauthor command run when the button is activated.
        IReadOnlyList<(string Name, string Command)> Buttons);

    /// <summary>A DVD-Video with a root menu, an optional scene menu, and one title.</summary>
    public sealed record MenuDvdSpec(
        string TitleMpeg,
        bool IsPal,
        string AspectRatio,
        IReadOnlyList<TimeSpan> ChapterStarts,
        IReadOnlyList<string> AudioLangs,
        IReadOnlyList<string> SubpictureLangs,
        MenuPgc RootMenu,
        // null when the disc has no chapters → no scene-selection menu.
        MenuPgc? SceneMenu);

    /// <summary>
    /// Build the dvdauthor XML for a menu DVD: a VMGM root menu (<c>entry="title"</c>),
    /// an optional VTSM scene menu (<c>entry="root"</c>), and the title with its
    /// chapter stops. The title returns to the root menu when it finishes.
    /// </summary>
    public static string BuildMenuXml(MenuDvdSpec spec)
    {
        string fmt    = spec.IsPal ? "pal" : "ntsc";
        string aspect = spec.AspectRatio;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<dvdauthor jumppad=\"1\">");

        // VMGM — the root menu (Play / Scenes).
        sb.AppendLine("  <vmgm>");
        sb.AppendLine("    <menus>");
        sb.AppendLine($"      <video format=\"{fmt}\" aspect=\"{aspect}\"/>");
        sb.AppendLine("      <pgc entry=\"title\">");
        sb.AppendLine($"        <vob file=\"{XmlEscape(spec.RootMenu.MpegFile)}\" pause=\"inf\"/>");
        foreach (var (name, cmd) in spec.RootMenu.Buttons)
            sb.AppendLine($"        <button name=\"{XmlEscape(name)}\">{XmlEscape(cmd)}</button>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </menus>");
        sb.AppendLine("  </vmgm>");

        // Titleset — optional scene menu, then the movie.
        sb.AppendLine("  <titleset>");
        if (spec.SceneMenu is not null)
        {
            sb.AppendLine("    <menus>");
            sb.AppendLine($"      <video format=\"{fmt}\" aspect=\"{aspect}\"/>");
            sb.AppendLine("      <pgc entry=\"root\">");
            sb.AppendLine($"        <vob file=\"{XmlEscape(spec.SceneMenu.MpegFile)}\" pause=\"inf\"/>");
            foreach (var (name, cmd) in spec.SceneMenu.Buttons)
                sb.AppendLine($"        <button name=\"{XmlEscape(name)}\">{XmlEscape(cmd)}</button>");
            sb.AppendLine("      </pgc>");
            sb.AppendLine("    </menus>");
        }
        sb.AppendLine("    <titles>");
        sb.AppendLine($"      <video format=\"{fmt}\" aspect=\"{aspect}\"/>");
        foreach (var lang in spec.AudioLangs)
            sb.AppendLine($"      <audio lang=\"{XmlEscape(lang)}\"/>");
        foreach (var lang in spec.SubpictureLangs)
            sb.AppendLine($"      <subpicture lang=\"{XmlEscape(lang)}\"/>");
        sb.AppendLine("      <pgc>");
        var chapters = FormatChapters(spec.ChapterStarts);
        if (chapters.Length > 0)
            sb.AppendLine($"        <vob file=\"{XmlEscape(spec.TitleMpeg)}\" chapters=\"{chapters}\"/>");
        else
            sb.AppendLine($"        <vob file=\"{XmlEscape(spec.TitleMpeg)}\"/>");
        // When the movie ends, go back to the root menu instead of into limbo.
        // dvdauthor requires `call` (not `jump`) for a title → menu transition.
        sb.AppendLine("        <post>call vmgm menu;</post>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </titles>");
        sb.AppendLine("  </titleset>");
        sb.AppendLine("</dvdauthor>");
        return sb.ToString();
    }

    /// <summary>
    /// Format chapter start times for a dvdauthor <c>chapters="..."</c> attribute:
    /// a comma-separated list of <c>H:MM:SS.fff</c>. A leading 00:00:00 is added
    /// if missing (dvdauthor wants chapter 1 at the title start), and the list is
    /// capped at the 99-chapters-per-PGC DVD limit. Returns "" for no chapters.
    /// </summary>
    public static string FormatChapters(IReadOnlyList<TimeSpan>? starts)
    {
        if (starts is null || starts.Count == 0) return "";

        var times = starts.OrderBy(t => t).ToList();
        if (times[0] > TimeSpan.Zero)
            times.Insert(0, TimeSpan.Zero);
        if (times.Count > 99)
            times = times.Take(99).ToList();

        return string.Join(",", times.Select(FormatTime));
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
