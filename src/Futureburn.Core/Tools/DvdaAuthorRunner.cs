using System.Diagnostics;
using System.Runtime.Versioning;

namespace Futureburn.Core.Tools;

// Wraps invocation of dvda-author.exe. Given a list of LPCM WAV files,
// produces a DVD-Audio AUDIO_TS\ folder with proper IFO/BUP/AOB files.
//
// dvda-author CLI shape:
//   dvda-author -W -o <output> -g track1.wav track2.wav track3.wav
// -g starts a group and is followed by SPACE-separated file paths (not
// commas — passing a comma-joined list makes dvda-author treat it as a
// single filename and bail with "n'est pas un fichier"). One group =
// one album entry; multiple -g flags are allowed for multi-group discs
// but we only emit one. -W disables config-file parsing so dvda-author
// doesn't try to fopen("dvda-author.conf", ...) from the CWD.

[SupportedOSPlatform("windows")]
public sealed class DvdaAuthorRunner
{
    public string ExePath { get; }
    public string VersionLine { get; }

    private DvdaAuthorRunner(string exePath, string versionLine)
    {
        ExePath = exePath;
        VersionLine = versionLine;
    }

    public static DvdaAuthorRunner? Locate()
    {
        var info = DvdaAuthorLocator.Locate();
        return info is null ? null : new DvdaAuthorRunner(info.Path, info.VersionLine);
    }

    public static DvdaAuthorRunner LocateOrThrow()
    {
        return Locate() ?? throw new InvalidOperationException(
            "dvda-author isn't installed on this system. It's not on winget / choco / scoop.\n" +
            "Download the Windows binary from https://sourceforge.net/projects/dvd-audio/\n" +
            "and put dvda-author.exe somewhere on PATH or under C:\\dvda-author\\bin\\.\n" +
            "Then run `futureburn dvda-author-info` to verify.");
    }

    /// <summary>
    /// Author a single-group DVD-Audio AUDIO_TS\ folder from the given LPCM WAVs.
    /// All inputs should be 16/20/24-bit, 44.1/48/88.2/96/176.4/192 kHz.
    /// </summary>
    public void AuthorSingleGroup(
        IReadOnlyList<string> wavInputs,
        string outputFolder,
        Action<string>? onLog = null)
    {
        if (wavInputs.Count == 0)
            throw new ArgumentException("Need at least one WAV input", nameof(wavInputs));

        var psi = new ProcessStartInfo
        {
            FileName               = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-W");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(outputFolder);
        psi.ArgumentList.Add("-g");
        foreach (var wav in wavInputs) psi.ArgumentList.Add(wav);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {ExePath}");

        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dvda-author exited with code {p.ExitCode}.");
    }
}
