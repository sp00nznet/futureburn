using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Futureburn.Core.Ffmpeg;

// Wrapper around ffprobe.exe (ships alongside ffmpeg). Runs
//   ffprobe -v error -hide_banner -print_format json -show_format -show_streams <input>
// and parses the JSON.
//
// ffprobe knows much more about audio/video files than NAudio — codec details,
// container info, bit rates, language tags, chapter markers, etc. We use it
// when available to enrich AudioInfo, and fall back to NAudio's basics when
// ffmpeg isn't installed.

[SupportedOSPlatform("windows")]
public static class FfprobeRunner
{
    public sealed record StreamInfo(
        int Index,
        string CodecType,
        string CodecName,
        string CodecLongName,
        int? SampleRate,
        int? Channels,
        long? BitRate,
        int? BitsPerRawSample,
        TimeSpan? Duration,
        int? Width,
        int? Height,
        string? Language,
        string? Title = null)
    {
        public bool IsVideo    => CodecType == "video";
        public bool IsAudio    => CodecType == "audio";
        public bool IsSubtitle => CodecType == "subtitle";
    }

    public sealed record FormatInfo(
        string FormatName,
        string FormatLongName,
        TimeSpan? Duration,
        long? BitRate,
        long? Size,
        IReadOnlyDictionary<string, string> Tags);

    /// <summary>One chapter marker (MKV / MP4 chapters, etc.).</summary>
    public sealed record ChapterInfo(
        int Id,
        TimeSpan Start,
        TimeSpan End,
        string? Title)
    {
        public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    }

    public sealed record ProbeResult(
        FormatInfo Format,
        IReadOnlyList<StreamInfo> Streams,
        IReadOnlyList<ChapterInfo> Chapters)
    {
        public IEnumerable<StreamInfo> VideoStreams    => Streams.Where(s => s.IsVideo);
        public IEnumerable<StreamInfo> AudioStreams    => Streams.Where(s => s.IsAudio);
        public IEnumerable<StreamInfo> SubtitleStreams => Streams.Where(s => s.IsSubtitle);
    }

    public static ProbeResult Probe(string input)
    {
        var ffprobe = LocateFfprobe()
            ?? throw new InvalidOperationException(
                "ffprobe isn't installed (it ships with ffmpeg). Install with `winget install Gyan.FFmpeg`.");

        var psi = new ProcessStartInfo
        {
            FileName               = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-v");           psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add("-show_chapters");
        psi.ArgumentList.Add(input);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Couldn't start ffprobe.");
        var json = p.StandardOutput.ReadToEnd();
        var err  = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffprobe exited {p.ExitCode}: {err.Trim()}");

        return Parse(json);
    }

    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fmtEl = root.GetProperty("format");
        var format = new FormatInfo(
            FormatName:     fmtEl.GetProperty("format_name").GetString() ?? "",
            FormatLongName: TryString(fmtEl, "format_long_name") ?? "",
            Duration:       TryDoubleSeconds(fmtEl, "duration"),
            BitRate:        TryLongString(fmtEl, "bit_rate"),
            Size:           TryLongString(fmtEl, "size"),
            Tags:           ExtractTags(fmtEl));

        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var sArr))
        {
            foreach (var s in sArr.EnumerateArray())
            {
                var tags = ExtractTags(s);
                streams.Add(new StreamInfo(
                    Index:           s.GetProperty("index").GetInt32(),
                    CodecType:       TryString(s, "codec_type")      ?? "",
                    CodecName:       TryString(s, "codec_name")      ?? "",
                    CodecLongName:   TryString(s, "codec_long_name") ?? "",
                    SampleRate:      TryIntString(s, "sample_rate"),
                    Channels:        TryInt(s, "channels"),
                    BitRate:         TryLongString(s, "bit_rate"),
                    BitsPerRawSample: TryIntString(s, "bits_per_raw_sample"),
                    Duration:        TryDoubleSeconds(s, "duration"),
                    Width:           TryInt(s, "width"),
                    Height:          TryInt(s, "height"),
                    Language:        tags.GetValueOrDefault("language"),
                    Title:           tags.GetValueOrDefault("title")));
            }
        }

        var chapters = new List<ChapterInfo>();
        if (root.TryGetProperty("chapters", out var cArr))
        {
            foreach (var c in cArr.EnumerateArray())
            {
                chapters.Add(new ChapterInfo(
                    Id:    TryInt(c, "id") ?? chapters.Count,
                    Start: TryDoubleSeconds(c, "start_time") ?? TimeSpan.Zero,
                    End:   TryDoubleSeconds(c, "end_time")   ?? TimeSpan.Zero,
                    Title: ExtractTags(c).GetValueOrDefault("title")));
            }
        }

        return new ProbeResult(format, streams, chapters);
    }

    private static string? LocateFfprobe()
    {
        // ffprobe lives in the same dir as ffmpeg.exe. Reuse the locator + swap the filename.
        var ffmpeg = Tools.FfmpegLocator.Locate();
        if (ffmpeg is null) return null;
        if (ffmpeg.Path == "ffmpeg")
        {
            // PATH-resolved name; assume ffprobe is on PATH too.
            return "ffprobe";
        }
        var dir = Path.GetDirectoryName(ffmpeg.Path);
        if (string.IsNullOrEmpty(dir)) return null;
        var probe = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(probe) ? probe : null;
    }

    private static string? TryString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? TryInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static int? TryIntString(JsonElement el, string name)
        => TryString(el, name) is { } s && int.TryParse(s, out var n) ? n : null;

    private static long? TryLongString(JsonElement el, string name)
        => TryString(el, name) is { } s && long.TryParse(s, out var n) ? n : null;

    private static TimeSpan? TryDoubleSeconds(JsonElement el, string name)
    {
        var s = TryString(el, name);
        if (s is null) return null;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return TimeSpan.FromSeconds(d);
        return null;
    }

    private static IReadOnlyDictionary<string, string> ExtractTags(JsonElement el)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    dict[p.Name] = p.Value.GetString() ?? "";
            }
        }
        return dict;
    }
}
