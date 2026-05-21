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
    /// Author a DVD-Video folder from a single title described by <paramref name="spec"/>.
    /// </summary>
    public void Author(DvdTitleSpec spec, string outputFolder, Action<string>? onLog = null)
    {
        var xml = BuildXml(spec);
        var xmlPath = Path.Combine(Path.GetTempPath(), $"futureburn-dvdauthor-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, xml);

        try
        {
            // dvdauthor -o outputFolder -x xmlPath. -o overrides any "dest" in
            // the XML, so we leave dest out and rely on -o.
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
