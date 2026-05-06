using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Wave;

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
    /// </summary>
    public static void DecodeToCdWav(string inputPath, string outputPath)
    {
        EnsureFileExists(inputPath);
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
