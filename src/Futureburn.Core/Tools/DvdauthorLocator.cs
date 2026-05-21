using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Tools;

// Locate dvdauthor on the system. Same pattern as FfmpegLocator: try PATH,
// then a few common install locations. Don't bundle (it's GPL — same
// licensing concern as ffmpeg for our MIT distribution).
//
// Why dvdauthor: writing fully-spec-compliant DVD-Video IFO/BUP files
// (the navigation tables every standalone DVD player reads) is its own
// multi-session subsystem. The IFO format has nested tables and the
// VTS_VOBU_ADMAP requires scanning the VOB for every NAV packet to find
// VOBU boundaries. dvdauthor — the canonical open-source DVD-Video
// authoring tool — does all of that. We shell out to it.
//
// User installs via:
//   choco install dvdauthor
//   scoop install dvdauthor   (extras bucket)
//   or download from https://dvdauthor.sourceforge.net/

[SupportedOSPlatform("windows")]
public static class DvdauthorLocator
{
    public sealed record DvdauthorInfo(string Path, string VersionLine);

    public static DvdauthorInfo? Locate()
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
        yield return "dvdauthor";   // PATH
        yield return "dvdauthor.exe";

        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // DVDStyler bundles dvdauthor.exe — easiest install path on Windows
        // since DVDStyler is on winget (AlexThuering.DVDStyler).
        foreach (var styler in ProgramFilesRoots())
        {
            var dvdStylerRoot = Path.Combine(styler, "DVDStyler");
            yield return Path.Combine(dvdStylerRoot, "bin",   "dvdauthor.exe");
            yield return Path.Combine(dvdStylerRoot, "tools", "dvdauthor.exe");
            yield return Path.Combine(dvdStylerRoot,          "dvdauthor.exe");
        }

        // Scoop shim (extras bucket has dvdauthor)
        var scoop = Environment.GetEnvironmentVariable("SCOOP");
        if (!string.IsNullOrEmpty(scoop))
        {
            yield return Path.Combine(scoop, "shims", "dvdauthor.exe");
            yield return Path.Combine(scoop, "apps",  "dvdauthor", "current", "dvdauthor.exe");
        }

        // Chocolatey shim (no current package, but kept for future)
        var choco = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrEmpty(choco))
        {
            yield return Path.Combine(choco, "bin", "dvdauthor.exe");
            yield return Path.Combine(choco, "lib", "dvdauthor", "tools", "bin", "dvdauthor.exe");
        }

        // Common manual install locations
        foreach (var root in ProgramFilesRoots())
            yield return Path.Combine(root, "dvdauthor", "bin", "dvdauthor.exe");
        if (!string.IsNullOrEmpty(lad)) yield return Path.Combine(lad, "Programs", "dvdauthor", "bin", "dvdauthor.exe");

        yield return @"C:\dvdauthor\bin\dvdauthor.exe";
    }

    /// <summary>
    /// Every plausible "Program Files" root. The CLI and GUI build as x86 (for
    /// the 32-bit LightScribe DLL), so under WOW64 <c>SpecialFolder.ProgramFiles</c>
    /// resolves to "Program Files (x86)" — we must also consult <c>%ProgramW6432%</c>,
    /// which always names the true 64-bit Program Files even from a 32-bit process.
    /// DVDStyler installs into the 64-bit tree, so without this it's never found.
    /// </summary>
    internal static IEnumerable<string> ProgramFilesRoots() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static DvdauthorInfo? TryRun(string pathOrName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pathOrName,
                Arguments              = "--help",   // dvdauthor with --help prints version on first line
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            // dvdauthor prints version+help on stderr on most builds.
            string output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            // dvdauthor exits with non-zero on --help in some builds; be lenient.

            // First line typically: "DVDAuthor::dvdauthor, version 0.7.2..."
            // or a usage line if --help triggered. Either way, output should contain "dvdauthor".
            if (!output.ToLowerInvariant().Contains("dvdauthor")) return null;
            var firstLine = output.Split('\n').FirstOrDefault(l => l.Contains("dvdauthor", StringComparison.OrdinalIgnoreCase))?.Trim()
                            ?? output.Split('\n', 2)[0].Trim();
            return new DvdauthorInfo(pathOrName, firstLine);
        }
        catch { return null; }
    }
}
