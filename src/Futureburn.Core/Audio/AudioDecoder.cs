using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Futureburn.Core.Ffmpeg;

namespace Futureburn.Core.Audio;

[SupportedOSPlatform("windows")]
public static class AudioDecoder
{
    // Run Media Foundation startup once on first class touch.
    // MediaFoundationApi.Startup() is idempotent but cheaper to gate.
    private static readonly bool _mfReady = InitMediaFoundation();
    private static bool InitMediaFoundation() { MediaFoundationApi.Startup(); return true; }

    // Extensions we accept. WAV is read directly; everything else goes through
    // Windows Media Foundation, which on Win10/11 covers MP3, AAC/M4A, WMA, FLAC.
    public static readonly IReadOnlyList<string> SupportedExtensions = new[]
    {
        ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac"
    };

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Read just the format/duration of an audio file. Doesn't decode the audio.
    /// </summary>
    public static AudioInfo Probe(string path)
    {
        EnsureFileExists(path);
        using var reader = OpenReader(path);
        return new AudioInfo(
            Path:               Path.GetFullPath(path),
            SampleRate:         reader.WaveFormat.SampleRate,
            Channels:           reader.WaveFormat.Channels,
            BitsPerSample:      reader.WaveFormat.BitsPerSample,
            Encoding:           reader.WaveFormat.Encoding.ToString(),
            Duration:           reader.TotalTime,
            EstimatedCdSectors: CdFormat.SectorsForDuration(reader.TotalTime));
    }

    /// <summary>
    /// Decode an audio file and write a CD-format WAV (44.1 kHz / 16-bit / stereo)
    /// to the destination path. If the input is already CD-format, we skip the
    /// resampler entirely.
    /// Tries NAudio (Windows Media Foundation) first; falls back to ffmpeg
    /// when NAudio either throws or produces a header-only WAV.
    /// </summary>
    public static void DecodeToCdWav(string inputPath, string outputPath)
    {
        EnsureFileExists(inputPath);

        Exception? naudioFailure = null;
        try
        {
            DecodeWithNAudio(inputPath, outputPath);
            // NAudio's MediaFoundationReader opens DASH-fragmented MP4s
            // (e.g. Spotify-downloaded m4a files where ftyp brand is "dash")
            // and reports correct duration, but Read() returns 0 immediately,
            // leaving us with a 44–50 byte header-only WAV. Detect that and
            // fall through to the ffmpeg fallback. 1 KB of CD-format audio
            // is ~6 ms — anything legitimate will exceed that.
            if (new FileInfo(outputPath).Length >= 1024) return;
        }
        catch (Exception ex)
        {
            naudioFailure = ex;
        }

        var ff = FfmpegRunner.Locate();
        if (ff is null)
        {
            var detail = naudioFailure?.Message
                         ?? "decoded to an empty WAV (likely a DASH-fragmented MP4 — common for Spotify m4a downloads)";
            throw new InvalidOperationException(
                $"Couldn't decode {Path.GetFileName(inputPath)}: {detail}. " +
                "Install ffmpeg (winget install Gyan.FFmpeg) so we can fall back to it for stubborn inputs.");
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);
        var result = ff.Run(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", inputPath,
            "-vn",                                       // strip cover art / video tracks
            "-ac", CdFormat.Channels.ToString(),
            "-ar", CdFormat.SampleRate.ToString(),
            "-sample_fmt", "s16",
            outputPath,
        });
        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
            throw new InvalidOperationException(
                $"ffmpeg fallback couldn't decode {Path.GetFileName(inputPath)}: {result.CombinedLog.Trim()}");
    }

    private static void DecodeWithNAudio(string inputPath, string outputPath)
    {
        using var reader = OpenReader(inputPath);
        if (IsAlreadyCdFormat(reader.WaveFormat))
        {
            WaveFileWriter.CreateWaveFile(outputPath, reader);
            return;
        }
        var target = new WaveFormat(CdFormat.SampleRate, CdFormat.BitsPerSample, CdFormat.Channels);
        using var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(outputPath, resampler);
    }

    private static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
            throw new NotSupportedException(
                $"Unsupported audio format '{ext}'. Supported: {string.Join(", ", SupportedExtensions)}");

        // WAV gets the lightweight reader; everything else goes through Media Foundation.
        return ext == ".wav"
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
    }

    private static bool IsAlreadyCdFormat(WaveFormat fmt)
        => fmt.SampleRate    == CdFormat.SampleRate
        && fmt.Channels      == CdFormat.Channels
        && fmt.BitsPerSample == CdFormat.BitsPerSample
        && fmt.Encoding      == WaveFormatEncoding.Pcm;

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Audio file not found: {path}", path);
    }
}

public sealed record AudioInfo(
    string Path,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string Encoding,
    TimeSpan Duration,
    long EstimatedCdSectors)
{
    public double DurationMinutes => Duration.TotalMinutes;

    public bool IsCdFormat
        => SampleRate == CdFormat.SampleRate
        && Channels == CdFormat.Channels
        && BitsPerSample == CdFormat.BitsPerSample
        && Encoding == "Pcm";
}
