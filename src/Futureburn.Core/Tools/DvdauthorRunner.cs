using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Futureburn.Core.Tools;

// Wrapper for invoking dvdauthor.exe to produce real spec-compliant
// DVD-Video IFO/BUP files from an MPEG-PS input.
//
// Usage:
//   var d = DvdauthorRunner.LocateOrThrow();
//   d.Author(mpegInput, outputFolder, onLog: line => ...);
//
// Output is laid down at outputFolder/VIDEO_TS/ + outputFolder/AUDIO_TS/.
// dvdauthor handles the entire IFO authoring (TT_SRPT, VTS_PGCI,
// VTS_C_ADT, VTS_VOBU_ADMAP scanning the VOB for NAV packets — all of
// it). The result is a folder that plays in any DVD player.

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
            "dvdauthor isn't installed on this system. Install with one of:\n" +
            "  choco install dvdauthor\n" +
            "  scoop install dvdauthor\n" +
            "Or download from https://dvdauthor.sourceforge.net/ and put dvdauthor.exe on PATH.\n" +
            "Then run `futureburn dvdauthor` to verify.");
    }

    /// <summary>
    /// Author a DVD-Video folder from a single MPEG-PS title. Builds a small
    /// XML control file describing one title set with one PGC and runs
    /// dvdauthor against it.
    /// </summary>
    public void AuthorSingleTitle(
        string mpegInputPath,
        string outputFolder,
        bool isPal = false,
        string aspectRatio = "4:3",
        Action<string>? onLog = null)
    {
        // Build the XML control file. dvdauthor accepts XML via -x; each path
        // in the XML can be absolute. We escape any awkward characters.
        var xml = BuildSingleTitleXml(outputFolder, mpegInputPath, isPal, aspectRatio);

        var xmlPath = Path.Combine(Path.GetTempPath(), $"futureburn-dvdauthor-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, xml);

        try
        {
            // dvdauthor -o outputFolder -x xmlPath
            // -o overrides any "dest" in the XML. We leave dest out and rely on -o.
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

    public static string BuildSingleTitleXml(
        string outputFolder, string mpegInput, bool isPal = false, string aspectRatio = "4:3")
    {
        // We need a <vmgm> section so dvdauthor runs its second pass and
        // produces VIDEO_TS.IFO/BUP (the master Volume Manager files). The
        // <vmgm><menus> needs <video format="..."> declared or dvdauthor
        // bails with "no video format specified for VMGM".
        //
        // The single PGC inside is a stub that just `jump title 1;`s — so
        // when the disc loads, the player runs the VMGM, which immediately
        // hands off to the title. That's the "auto-play on insert" behavior
        // standalone DVD players (PS4 etc.) expect.
        //
        // jumppad="yes" makes dvdauthor insert the small connector cells
        // some players want for clean transitions.
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<dvdauthor jumppad=\"yes\">");
        sb.AppendLine("  <vmgm>");
        sb.AppendLine("    <menus>");
        sb.AppendLine($"      <video format=\"{(isPal ? "pal" : "ntsc")}\" aspect=\"{aspectRatio}\"/>");
        sb.AppendLine("      <pgc entry=\"title\">");
        sb.AppendLine("        <pre>jump title 1;</pre>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </menus>");
        sb.AppendLine("  </vmgm>");
        sb.AppendLine("  <titleset>");
        sb.AppendLine("    <titles>");
        sb.AppendLine("      <pgc>");
        sb.AppendLine($"        <vob file=\"{XmlEscape(mpegInput)}\"/>");
        sb.AppendLine("      </pgc>");
        sb.AppendLine("    </titles>");
        sb.AppendLine("  </titleset>");
        sb.AppendLine("</dvdauthor>");
        return sb.ToString();
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
