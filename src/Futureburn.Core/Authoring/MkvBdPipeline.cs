using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Futureburn.Core.Ffmpeg;
using Futureburn.Core.Tools;

namespace Futureburn.Core.Authoring;

// The MKV → Blu-ray (BD-Video) authoring pipeline: take any video file and
// produce a UDF 2.50 Blu-ray ISO (or BDMV folder) that plays in a standalone
// player / PS-console disc slot.
//
// Division of labour, deliberately lazy:
//   tsMuxeR does the Blu-ray-specific hard parts — muxing to .m2ts, writing
//   the BDMV metadata (index.bdmv, .mpls playlists, .clpi seek indexes),
//   rendering SRT text into PGS graphic subtitles, laying down chapters, and
//   finalizing the UDF 2.50 image. We don't reimplement any of that.
//   ffmpeg only conforms streams that aren't already Blu-ray-legal:
//     - video   : re-encode to H.264 High@4.1 and pad to a legal frame size
//     - audio   : transcode non-BD codecs (AAC/FLAC/Opus/MP3/...) to AC-3
//     - subs    : extract text subs to SRT for tsMuxeR to render as PGS
//   Streams that are already legal are referenced straight from the source —
//   no re-encode, no quality loss (the 101 Dalmatians / most Blu-ray rips case).
//
// Flow: analyze (tsMuxeR) + probe chapters (ffprobe) → conform what needs it
//       (ffmpeg) → write meta → tsMuxeR muxes to ISO.

[SupportedOSPlatform("windows")]
public static class MkvBdPipeline
{
    // Legal Blu-ray progressive/interlaced frame sizes.
    private static readonly (int W, int H)[] LegalSizes =
        { (1920, 1080), (1440, 1080), (1280, 720), (720, 480), (720, 576) };

    // Legal Blu-ray frame rates (×1000, to compare tsMuxeR's rounded values).
    private static readonly int[] LegalFps1000 = { 23976, 24000, 25000, 29970, 50000, 59940 };

    // Audio codecs Blu-ray players accept natively (tsMuxeR stream tags).
    private static readonly HashSet<string> LegalAudioIds = new(StringComparer.OrdinalIgnoreCase)
        { "A_AC3", "A_DTS", "A_LPCM", "A_MLP" };

    public const int MaxAudio = 8;
    public const int MaxSubs  = 8;

    public sealed record Options(
        string InputVideo,
        string OutputIso,      // path ending in .iso, or a folder for a BDMV tree
        string? Label = null);

    public sealed record Probed(
        string VideoCodec,
        int Width,
        int Height,
        double FrameRate,
        bool VideoLegal,
        TimeSpan Duration,
        IReadOnlyList<string> AudioTracks,       // "DTS 5.1 eng" ...
        IReadOnlyList<string> SubtitleTracks,    // "PGS eng", "SRT eng" ...
        int Chapters,
        bool NeedsConform);

    public sealed record AuthorResult(
        string OutputIso,
        long OutputBytes,
        bool ReencodedVideo,
        int AudioTracks,
        int TranscodedAudio,
        int Subtitles,
        int Chapters);

    /// <summary>Probe the input and report what the pipeline would do.</summary>
    public static Probed Probe(string inputVideo)
    {
        var tsm = TsMuxerRunner.LocateOrThrow();
        var tracks = tsm.Analyze(inputVideo);
        var video = tracks.FirstOrDefault(t => t.Kind == "video")
            ?? throw new InvalidOperationException("Input has no video stream — can't author a Blu-ray.");

        var probe = FfprobeRunner.Probe(inputVideo);
        var audio = tracks.Where(t => t.Kind == "audio").Take(MaxAudio).ToList();
        var subs  = tracks.Where(t => t.Kind == "subtitle" && IsSupportedSub(t)).Take(MaxSubs).ToList();

        bool vLegal = VideoLegal(video);
        bool needsConform = !vLegal
            || audio.Any(a => !LegalAudioIds.Contains(a.StreamId))
            || subs.Any(s => s.StreamId.Equals("S_TEXT/UTF8", StringComparison.OrdinalIgnoreCase));

        return new Probed(
            VideoCodec:  video.StreamType,
            Width:       video.Width  ?? 0,
            Height:      video.Height ?? 0,
            FrameRate:   video.FrameRate ?? 0,
            VideoLegal:  vLegal,
            Duration:    probe.Format.Duration ?? TimeSpan.Zero,
            AudioTracks: audio.Select(a =>
                $"{a.StreamType} {ChannelsText(a.Channels10)} {a.Lang ?? "und"}").ToList(),
            SubtitleTracks: subs.Select(s => $"{s.StreamType} {s.Lang ?? "und"}").ToList(),
            Chapters:    probe.Chapters.Count,
            NeedsConform: needsConform);
    }

    /// <summary>Run the pipeline, producing the Blu-ray ISO at Options.OutputIso.</summary>
    public static AuthorResult Author(
        Options opts,
        Action<string>? onLog = null,
        Action<double>? onProgress = null)
    {
        var tsm = TsMuxerRunner.LocateOrThrow();
        var tracks = tsm.Analyze(opts.InputVideo);
        var video = tracks.FirstOrDefault(t => t.Kind == "video")
            ?? throw new InvalidOperationException("Input has no video stream.");
        var audioTracks = tracks.Where(t => t.Kind == "audio").Take(MaxAudio).ToList();
        var subTracks   = tracks.Where(t => t.Kind == "subtitle" && IsSupportedSub(t)).Take(MaxSubs).ToList();

        var probe = FfprobeRunner.Probe(opts.InputVideo);
        var chapterStarts = probe.Chapters.Select(c => c.Start)
            .Where(s => s > TimeSpan.Zero).Distinct().OrderBy(s => s).ToList();

        bool vLegal = VideoLegal(video);
        bool anyConform = !vLegal
            || audioTracks.Any(a => !LegalAudioIds.Contains(a.StreamId))
            || subTracks.Any(IsTextSub);

        var tempDir = Path.Combine(Path.GetTempPath(), $"futureburn-bd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Conform is the slow phase when present; give it the first 70% of the bar.
        double muxBase = anyConform ? 0.70 : 0.0;
        double muxSpan = 1.0 - muxBase;

        int transcodedAudio = 0;
        try
        {
            FfmpegRunner? ffmpeg = anyConform ? FfmpegRunner.LocateOrThrow() : null;

            // Target frame size for a video re-encode (drives subtitle rendering too).
            var (targetW, targetH) = vLegal
                ? (video.Width ?? 1920, video.Height ?? 1080)
                : ChooseTargetSize(video);
            double targetFps = LegalFps(video.FrameRate) ?? 23.976;

            var meta = new StringBuilder();
            meta.Append("MUXOPT --blu-ray --vbr --vbv-len=500");
            var effectiveChapters = chapterStarts.Count >= 2 ? chapterStarts : AutoChapters(probe.Format.Duration ?? TimeSpan.Zero);
            if (effectiveChapters.Count >= 2)
                meta.Append(" --custom-chapters=" + string.Join(";", effectiveChapters.Select(FormatTime)));
            meta.Append('\n');

            // --- Video line ---------------------------------------------------
            bool reencoded = false;
            if (vLegal)
            {
                meta.Append($"V_MPEG4/ISO/AVC, \"{opts.InputVideo}\", insertSEI, contSPS, track={video.Id}, lang={Lang(video.Lang)}\n");
            }
            else
            {
                onLog?.Invoke($"Video isn't Blu-ray-legal ({video.StreamType} {video.Width}x{video.Height} @ {video.FrameRate}); " +
                              $"re-encoding to H.264 {targetW}x{targetH} ...");
                var vOut = Path.Combine(tempDir, "video.mp4");
                ReencodeVideo(ffmpeg!, opts.InputVideo, vOut, targetW, targetH, targetFps,
                    probe.Format.Duration ?? TimeSpan.Zero,
                    onLog, frac => onProgress?.Invoke(frac * muxBase * 0.85));
                meta.Append($"V_MPEG4/ISO/AVC, \"{vOut}\", insertSEI, contSPS, track=1, lang={Lang(video.Lang)}\n");
                reencoded = true;
            }

            // --- Audio lines --------------------------------------------------
            int audioOrdinal = 0;  // ffmpeg 0:a:<n>, in tsMuxeR/container order
            foreach (var a in audioTracks)
            {
                if (LegalAudioIds.Contains(a.StreamId))
                {
                    meta.Append($"{a.StreamId}, \"{opts.InputVideo}\", track={a.Id}, lang={Lang(a.Lang)}\n");
                }
                else
                {
                    onLog?.Invoke($"Audio track {a.Id} ({a.StreamType}) isn't a Blu-ray codec; transcoding to AC-3 ...");
                    var aOut = Path.Combine(tempDir, $"audio{audioOrdinal}.ac3");
                    TranscodeAudioToAc3(ffmpeg!, opts.InputVideo, audioOrdinal, aOut, onLog);
                    meta.Append($"A_AC3, \"{aOut}\", lang={Lang(a.Lang)}\n");
                    transcodedAudio++;
                }
                audioOrdinal++;
            }

            // --- Subtitle lines ----------------------------------------------
            // Iterate ALL subtitle streams so ffSubIdx (the ffmpeg 0:s:<n>
            // ordinal) stays correct even when unsupported subs (e.g. VobSub)
            // sit between the supported ones; we just skip the unsupported.
            var allSubs = tracks.Where(t => t.Kind == "subtitle").ToList();
            int subsWritten = 0;
            for (int ffSubIdx = 0; ffSubIdx < allSubs.Count && subsWritten < MaxSubs; ffSubIdx++)
            {
                var s = allSubs[ffSubIdx];
                if (s.StreamId.StartsWith("S_HDMV/PGS", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Append($"S_HDMV/PGS, \"{opts.InputVideo}\", track={s.Id}, lang={Lang(s.Lang)}\n");
                    subsWritten++;
                }
                else if (IsTextSub(s))
                {
                    var srt = Path.Combine(tempDir, $"sub{ffSubIdx}.srt");
                    if (ExtractSrt(ffmpeg!, opts.InputVideo, ffSubIdx, srt, onLog))
                    {
                        // tsMuxeR renders SRT → PGS for --blu-ray; it needs the
                        // target canvas + fps to lay the bitmaps out.
                        meta.Append($"S_TEXT/UTF8, \"{srt}\", font-name=\"Arial\", font-size=48, " +
                                    $"font-color=0x00ffffff, bottom-offset=30, font-border=2, text-align=center, " +
                                    $"video-width={targetW}, video-height={targetH}, fps={targetFps.ToString(CultureInfo.InvariantCulture)}, " +
                                    $"lang={Lang(s.Lang)}\n");
                        subsWritten++;
                    }
                }
                // else: unsupported subtitle kind — skip (index still advances).
            }

            // --- Write meta + mux --------------------------------------------
            var metaPath = Path.Combine(tempDir, "author.meta");
            File.WriteAllText(metaPath, meta.ToString());
            onLog?.Invoke($"Muxing Blu-ray via tsMuxeR ({tsm.VersionLine}) ...");
            tsm.Mux(metaPath, opts.OutputIso,
                onLog: onLog,
                onProgress: frac => onProgress?.Invoke(muxBase + frac * muxSpan));
            onProgress?.Invoke(1.0);

            long outBytes = File.Exists(opts.OutputIso) ? new FileInfo(opts.OutputIso).Length : 0;
            return new AuthorResult(
                OutputIso:       opts.OutputIso,
                OutputBytes:     outBytes,
                ReencodedVideo:  reencoded,
                AudioTracks:     audioTracks.Count,
                TranscodedAudio: transcodedAudio,
                Subtitles:       subsWritten,
                Chapters:        effectiveChapters.Count >= 2 ? effectiveChapters.Count : 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // --- conform steps -------------------------------------------------------

    private static void ReencodeVideo(FfmpegRunner ff, string input, string output,
        int w, int h, double fps, TimeSpan total,
        Action<string>? onLog, Action<double>? onProgress)
    {
        int keyint = Math.Max(1, (int)Math.Round(fps));
        var vf = $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                 $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1";
        var x264 = $"bluray-compat=1:keyint={keyint}:open-gop=0:bframes=3:ref=3:" +
                   $"vbv-maxrate=25000:vbv-bufsize=25000:nal-hrd=vbr:aud=1";
        var args = new[]
        {
            "-y", "-i", input, "-map", "0:v:0",
            "-vf", vf,
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-profile:v", "high", "-level", "4.1",
            "-preset", "medium", "-crf", "18",
            "-maxrate", "25000k", "-bufsize", "25000k",
            "-x264-params", x264,
            "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-an", "-sn",
            output,
        };
        var rr = ff.Run(args, line =>
        {
            if (!line.StartsWith("frame=")) onLog?.Invoke(line);
            if (total > TimeSpan.Zero && MkvDvdPipeline.ParseFfmpegTime(line) is { } t)
                onProgress?.Invoke(Math.Clamp(t.TotalSeconds / total.TotalSeconds, 0, 1));
        });
        if (rr.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"ffmpeg video re-encode failed (exit {rr.ExitCode}).");
    }

    private static void TranscodeAudioToAc3(FfmpegRunner ff, string input, int audioOrdinal,
        string output, Action<string>? onLog)
    {
        var rr = ff.Run(new[]
        {
            "-y", "-i", input, "-map", $"0:a:{audioOrdinal}",
            "-c:a", "ac3", "-b:a", "640k", "-ac", "6",
            output,
        }, line => { if (!line.StartsWith("frame=") && !line.StartsWith("size=")) onLog?.Invoke(line); });
        if (rr.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"ffmpeg AC-3 transcode failed (exit {rr.ExitCode}).");
    }

    private static bool ExtractSrt(FfmpegRunner ff, string input, int subOrdinal,
        string output, Action<string>? onLog)
    {
        var rr = ff.Run(new[]
        {
            "-y", "-i", input, "-map", $"0:s:{subOrdinal}", "-c:s", "srt", output,
        });
        if (rr.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            onLog?.Invoke($"  subtitle {subOrdinal}: couldn't extract to SRT, skipped.");
            return false;
        }
        return true;
    }

    // --- legality helpers ----------------------------------------------------

    private static bool VideoLegal(TsMuxerRunner.Track v)
    {
        if (!v.StreamId.Equals("V_MPEG4/ISO/AVC", StringComparison.OrdinalIgnoreCase)) return false;
        if (v.Level10 is { } lvl && lvl > 41) return false;
        if (v.Width is not { } w || v.Height is not { } h) return false;
        if (!LegalSizes.Contains((w, h))) return false;
        if (LegalFps(v.FrameRate) is null) return false;
        return true;
    }

    private static double? LegalFps(double? fps)
    {
        if (fps is not { } f) return null;
        int f1000 = (int)Math.Round(f * 1000);
        return LegalFps1000.Any(l => Math.Abs(l - f1000) <= 30) ? f : (double?)null;
    }

    private static (int W, int H) ChooseTargetSize(TsMuxerRunner.Track v)
        => (v.Height ?? 1080) <= 720 && (v.Width ?? 1920) <= 1280 ? (1280, 720) : (1920, 1080);

    private static bool IsSupportedSub(TsMuxerRunner.Track t)
        => t.StreamId.StartsWith("S_HDMV/PGS", StringComparison.OrdinalIgnoreCase) || IsTextSub(t);

    private static bool IsTextSub(TsMuxerRunner.Track t)
        => t.StreamId.Equals("S_TEXT/UTF8", StringComparison.OrdinalIgnoreCase);

    private static List<TimeSpan> AutoChapters(TimeSpan duration)
    {
        var marks = new List<TimeSpan>();
        if (duration <= TimeSpan.FromMinutes(6)) return marks;
        double interval = Math.Max(300, duration.TotalSeconds / 10);  // ~5 min or 10 chapters
        for (double t = interval; t < duration.TotalSeconds - 60; t += interval)
            marks.Add(TimeSpan.FromSeconds(t));
        return marks;
    }

    private static string FormatTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    private static string Lang(string? l) => string.IsNullOrWhiteSpace(l) ? "und" : l.Trim();

    private static string ChannelsText(int? ch10) => ch10 switch
    {
        null => "",
        20 => "2.0", 10 => "1.0", 51 or 50 => "5.1", 61 or 60 => "6.1", 71 or 70 => "7.1",
        _  => (ch10.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture),
    };
}
