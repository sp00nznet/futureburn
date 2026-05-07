using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Ffmpeg;

// Thin wrapper around invoking ffmpeg.exe as a child process. We only locate
// it on demand — we do NOT bundle ffmpeg with futureburn (LGPL/GPL licensing
// is messy for an MIT distribution). When ffmpeg is needed and missing, we
// surface a clear "install via winget/choco/scoop" message.
//
// Usage pattern:
//   var ff = FfmpegRunner.LocateOrThrow();
//   ff.Run(new[] { "-i", input, "-c:a", "copy", output },
//          onLog: line => Console.WriteLine(line));
//
// stderr (where ffmpeg writes its progress + log) is captured line-by-line
// and forwarded to onLog. ffmpeg's own progress format is "frame=... time=..."
// — callers can parse that out of the log lines if they want a progress bar.

[SupportedOSPlatform("windows")]
public sealed class FfmpegRunner
{
    public string ExePath { get; }
    public string VersionLine { get; }

    private FfmpegRunner(string exePath, string versionLine)
    {
        ExePath = exePath;
        VersionLine = versionLine;
    }

    /// <summary>
    /// Locate ffmpeg via the same search paths as <c>FfmpegLocator</c>.
    /// Returns null if not found.
    /// </summary>
    public static FfmpegRunner? Locate()
    {
        var info = Tools.FfmpegLocator.Locate();
        return info is null ? null : new FfmpegRunner(info.Path, info.VersionLine);
    }

    /// <summary>Same as Locate but throws a friendly exception when missing.</summary>
    public static FfmpegRunner LocateOrThrow()
    {
        return Locate() ?? throw new InvalidOperationException(
            "ffmpeg isn't installed on this system. Install with one of:\n" +
            "  winget install Gyan.FFmpeg\n" +
            "  choco install ffmpeg\n" +
            "  scoop install ffmpeg\n" +
            "Then run `futureburn ffmpeg` to verify.");
    }

    public sealed record RunResult(int ExitCode, string CombinedLog);

    /// <summary>
    /// Run ffmpeg with the given arguments. <paramref name="onLog"/> is invoked
    /// for each line on stderr (ffmpeg's normal logging stream); the same lines
    /// are also accumulated into the returned <see cref="RunResult"/>.
    /// </summary>
    public RunResult Run(IEnumerable<string> args, Action<string>? onLog = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sb = new System.Text.StringBuilder();
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {ExePath}");

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sb.AppendLine(e.Data);
            onLog?.Invoke(e.Data);
        };
        p.BeginErrorReadLine();
        // Don't read stdout line-by-line; ffmpeg uses stderr for logging.
        // We do drain it so the pipe doesn't fill.
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new RunResult(p.ExitCode, sb.ToString());
    }
}
