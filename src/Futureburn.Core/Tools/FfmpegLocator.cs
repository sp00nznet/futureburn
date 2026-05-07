using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Tools;

// Locate ffmpeg on the system. ffmpeg is the foundation for any future
// video-disc authoring (DVD-Video, VCD, SVCD): MPEG-1/2 transcoding,
// AC-3 / MP2 / LPCM audio encoding, MPEG-PS multiplexing — all the
// non-trivial pipeline pieces. We won't reimplement those.
//
// Strategy: try `ffmpeg` on PATH, then a list of common install locations,
// then ask the user to install it. We do NOT bundle ffmpeg in the repo —
// licensing (LGPL/GPL depending on flags) makes that messy for an MIT
// project. Instead we detect what's there and call it as an external tool.

[SupportedOSPlatform("windows")]
public static class FfmpegLocator
{
    public sealed record FfmpegInfo(string Path, string VersionLine);

    public static FfmpegInfo? Locate()
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
        yield return "ffmpeg";  // hits PATH

        var pf      = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var lad     = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(pf))    yield return Path.Combine(pf,    "ffmpeg", "bin", "ffmpeg.exe");
        if (!string.IsNullOrEmpty(pfx86)) yield return Path.Combine(pfx86, "ffmpeg", "bin", "ffmpeg.exe");
        if (!string.IsNullOrEmpty(lad))   yield return Path.Combine(lad,   "Programs", "ffmpeg", "bin", "ffmpeg.exe");

        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";

        // Chocolatey shim
        var choco = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrEmpty(choco))
            yield return Path.Combine(choco, "bin", "ffmpeg.exe");

        // Scoop shim
        var scoop = Environment.GetEnvironmentVariable("SCOOP");
        if (!string.IsNullOrEmpty(scoop))
            yield return Path.Combine(scoop, "shims", "ffmpeg.exe");

        // winget Microsoft Store shim (added to PATH after shell restart;
        // we still won't find it via "ffmpeg" until then, but the alias
        // file is where new shells will pick it up).
        if (!string.IsNullOrEmpty(lad))
            yield return Path.Combine(lad, "Microsoft", "WindowsApps", "ffmpeg.exe");

        // winget Gyan.FFmpeg full-build install. The actual exe is buried
        // under a versioned subfolder (e.g. ffmpeg-8.1.1-full_build); enumerate.
        if (!string.IsNullOrEmpty(lad))
        {
            var wingetRoot = Path.Combine(lad, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetRoot))
            {
                foreach (var pkgDir in SafeEnumerateDirs(wingetRoot, "Gyan.FFmpeg*"))
                foreach (var buildDir in SafeEnumerateDirs(pkgDir, "ffmpeg-*"))
                {
                    yield return Path.Combine(buildDir, "bin", "ffmpeg.exe");
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string parent, string pattern)
    {
        try { return Directory.EnumerateDirectories(parent, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static FfmpegInfo? TryRun(string pathOrName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pathOrName,
                Arguments              = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;

            // First line is "ffmpeg version 6.1.1 Copyright (c) ..." or similar.
            var firstLine = output.Split('\n', 2)[0].Trim();
            return new FfmpegInfo(pathOrName, firstLine);
        }
        catch { return null; }
    }
}
