using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Tools;

// Locate dvda-author on the system. Same pattern as DvdauthorLocator;
// dvda-author is the canonical open-source DVD-Audio authoring tool
// (separate project from dvdauthor — different format, similar role).
//
// dvda-author is much harder to install than dvdauthor in 2026:
//   - NOT on winget
//   - NOT on chocolatey
//   - NOT on scoop main bucket
//   - The Sourceforge release (https://sourceforge.net/projects/dvd-audio/)
//     has Windows binaries but they're old and require manual download.
// We document the manual install path in the `dvda-author` info command.
//
// Reality check for users: the PS4 does NOT play DVD-Audio discs (it's
// DVD-Video + Blu-ray only). Authored DVD-A discs need a DVD-A-aware
// player — specific home theater units / older audiophile gear from the
// early 2000s. For "modern player plays a hi-res music disc," a data DVD
// with WAV / FLAC files via `burn-folder` is the practical answer.

[SupportedOSPlatform("windows")]
public static class DvdaAuthorLocator
{
    public sealed record DvdaAuthorInfo(string Path, string VersionLine);

    public static DvdaAuthorInfo? Locate()
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
        yield return "dvda-author";
        yield return "dvda-author.exe";

        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var lad   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Common manual install locations
        foreach (var root in new[] { pf, pfx86, lad })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "dvda-author", "bin", "dvda-author.exe");
            yield return Path.Combine(root, "dvda-author",        "dvda-author.exe");
        }

        yield return @"C:\dvda-author\bin\dvda-author.exe";
        yield return @"C:\dvda-author\dvda-author.exe";

        // Chocolatey (no current package, but kept for symmetry)
        var choco = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrEmpty(choco))
            yield return Path.Combine(choco, "bin", "dvda-author.exe");
    }

    private static DvdaAuthorInfo? TryRun(string pathOrName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pathOrName,
                Arguments              = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            // dvda-author writes its name + version on the first line; be lenient
            // about exit code since --version sometimes exits non-zero.
            if (!output.ToLowerInvariant().Contains("dvda")) return null;
            var firstLine = output.Split('\n').FirstOrDefault(l => l.Contains("dvda", StringComparison.OrdinalIgnoreCase))?.Trim()
                            ?? output.Split('\n', 2)[0].Trim();
            return new DvdaAuthorInfo(pathOrName, firstLine);
        }
        catch { return null; }
    }
}
