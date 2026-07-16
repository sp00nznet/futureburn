using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Tools;

// Locate tsMuxeR — the tool that builds a Blu-ray (BDMV) folder / UDF 2.50 ISO
// from elementary/container streams. Writing the BDMV metadata (index.bdmv,
// the .mpls playlists, the .clpi clip-info seek indexes) plus rendering SRT
// text into PGS graphic subtitles is a big binary-format subsystem — tsMuxeR
// is the canonical open-source tool that does all of it, so we shell out.
//
// Same "locate, don't bundle" policy as ffmpeg/dvdauthor. tsMuxeR has no
// installer — it's a single portable .exe — so besides PATH we look next to
// our own binary, a tools/ subfolder, and the usual manual-drop locations.
// Install: download tsMuxer-<ver>-win64.zip from
//   https://github.com/justdan96/tsMuxer/releases
// and drop tsMuxeR.exe on PATH (or beside futureburn.exe).

[SupportedOSPlatform("windows")]
public static class TsMuxerLocator
{
    public sealed record TsMuxerInfo(string Path, string VersionLine);

    public static TsMuxerInfo? Locate()
    {
        foreach (var candidate in CandidatePaths())
        {
            var info = TryRun(candidate);
            if (info is not null) return info;
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return "tsMuxeR";      // PATH
        yield return "tsMuxeR.exe";
        yield return "tsmuxer";
        yield return "tsmuxer.exe";

        // Beside our own executable, and a tools/ subfolder next to it — the
        // easiest place for a user to drop the portable exe.
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            yield return Path.Combine(baseDir, "tsMuxeR.exe");
            yield return Path.Combine(baseDir, "tools", "tsMuxeR.exe");
        }

        foreach (var root in DvdauthorLocator.ProgramFilesRoots())
        {
            yield return Path.Combine(root, "tsMuxeR", "tsMuxeR.exe");
            yield return Path.Combine(root, "tsMuxer", "tsMuxeR.exe");
        }

        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(lad))
        {
            yield return Path.Combine(lad, "Programs", "tsMuxeR", "tsMuxeR.exe");
            yield return Path.Combine(lad, "tsMuxeR", "tsMuxeR.exe");
        }

        var scoop = Environment.GetEnvironmentVariable("SCOOP");
        if (!string.IsNullOrEmpty(scoop))
            yield return Path.Combine(scoop, "shims", "tsmuxer.exe");

        yield return @"C:\tsMuxeR\tsMuxeR.exe";
        yield return @"C:\Program Files\tsMuxeR\tsMuxeR.exe";
    }

    private static TsMuxerInfo? TryRun(string pathOrName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pathOrName,
                // No args => tsMuxeR prints its version banner + usage and exits.
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }

            // First line: "tsMuxeR version 2.7.0. github.com/justdan96/tsMuxer"
            var firstLine = output.Split('\n', 2)[0].Trim();
            if (!firstLine.Contains("tsMuxeR", StringComparison.OrdinalIgnoreCase)) return null;
            return new TsMuxerInfo(pathOrName, firstLine);
        }
        catch { return null; }
    }
}
