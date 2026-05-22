using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Futureburn.Core.Ffmpeg;
using Futureburn.Core.Tools;

namespace Futureburn.Core.Authoring;

// The MKV → DVD-Video authoring pipeline: take any video file (MKV, MP4, ...)
// and produce a DVD-Video folder (VIDEO_TS\ + AUDIO_TS\), carrying chapters,
// every audio track, and text subtitles.
//
// This is the orchestration that used to live inline in the CLI; it's here in
// Core so the CLI and the WPF GUI both drive the same code. Burning the
// resulting folder is a separate step (FsImageBuilder + SptiDataBurner).
//
// Flow: probe → ffmpeg transcode (video + all audio → MPEG-PS) → spumux each
// text subtitle into the stream → dvdauthor authors the IFOs.

[SupportedOSPlatform("windows")]
public static class MkvDvdPipeline
{
    // DVD-Video stream limits.
    public const int MaxAudioStreams      = 8;
    public const int MaxSubpictureStreams = 32;

    public sealed record Options(
        string InputVideo,
        string OutputFolder,
        bool IsPal = false,
        string? Label = null,
        // Force VLC-only skeleton IFOs even if dvdauthor is installed (debug).
        bool SkeletonOnly = false);

    /// <summary>What a probe found in the input — shown before authoring.</summary>
    public sealed record Probed(
        string VideoCodec,
        int Width,
        int Height,
        string Aspect,
        TimeSpan Duration,
        IReadOnlyList<string> AudioLanguages,
        IReadOnlyList<string> TextSubtitleLanguages,
        int BitmapSubtitlesSkipped,
        int Chapters);

    /// <summary>What the pipeline actually produced.</summary>
    public sealed record AuthorResult(
        bool UsedDvdauthor,
        int AudioTracks,
        int SubtitlesMuxed,
        int Chapters,
        string Aspect,
        string OutputFolder);

    /// <summary>
    /// Probe the input video and report what the pipeline would carry over.
    /// Throws if there's no video stream or the input can't be read.
    /// </summary>
    public static Probed Probe(string inputVideo)
    {
        var probe = FfprobeRunner.Probe(inputVideo);
        var video = probe.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Input has no video stream — can't author a DVD-Video.");

        var audio = probe.AudioStreams.Take(MaxAudioStreams).ToList();
        var subs  = probe.SubtitleStreams.ToList();
        var textSubs = subs.Where(s => IsTextSubtitle(s.CodecName)).ToList();

        return new Probed(
            VideoCodec:             video.CodecName,
            Width:                  video.Width  ?? 0,
            Height:                 video.Height ?? 0,
            Aspect:                 GuessAspect(video),
            Duration:               probe.Format.Duration ?? TimeSpan.Zero,
            AudioLanguages:         audio.Select(a => a.Language ?? "und").ToList(),
            TextSubtitleLanguages:  textSubs.Take(MaxSubpictureStreams)
                                            .Select(s => s.Language ?? "und").ToList(),
            BitmapSubtitlesSkipped: subs.Count - textSubs.Count,
            Chapters:               probe.Chapters.Count);
    }

    /// <summary>
    /// Run the full authoring pipeline into <see cref="Options.OutputFolder"/>.
    /// <paramref name="onLog"/> receives every tool output line; <paramref name="onProgress"/>
    /// receives an overall 0..1 fraction (transcode is the bulk of it).
    /// </summary>
    public static AuthorResult Author(
        Options opts,
        Action<string>? onLog = null,
        Action<double>? onProgress = null)
    {
        var ffmpeg = FfmpegRunner.Locate()
            ?? throw new InvalidOperationException(
                "ffmpeg not found. Install with: winget install Gyan.FFmpeg");

        var probe = FfprobeRunner.Probe(opts.InputVideo);
        var video = probe.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException("Input has no video stream.");

        var audioStreams = probe.AudioStreams.Take(MaxAudioStreams).ToList();
        var subList = probe.SubtitleStreams.ToList();
        var textSubs = new List<(int SubIndex, FfprobeRunner.StreamInfo Stream)>();
        for (int i = 0; i < subList.Count; i++)
            if (IsTextSubtitle(subList[i].CodecName))
                textSubs.Add((i, subList[i]));
        if (textSubs.Count > MaxSubpictureStreams)
            textSubs = textSubs.Take(MaxSubpictureStreams).ToList();

        var chapterStarts = probe.Chapters.Select(c => c.Start).ToList();
        string aspect = GuessAspect(video);
        var totalDuration = probe.Format.Duration ?? TimeSpan.Zero;
        string label = opts.Label ?? Path.GetFileNameWithoutExtension(opts.InputVideo);

        var dvdauthor = opts.SkeletonOnly ? null : DvdauthorRunner.Locate();

        var tempDir = Path.Combine(Path.GetTempPath(), $"futureburn-dvdv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempMpg = Path.Combine(tempDir, "title.mpg");

        try
        {
            // --- Transcode: video + every audio track → one MPEG-PS. (0 .. 0.80)
            onLog?.Invoke($"Transcoding via ffmpeg ({ffmpeg.VersionLine}) ...");
            var ffargs = new List<string> { "-y", "-i", opts.InputVideo, "-map", "0:v:0" };
            for (int i = 0; i < audioStreams.Count; i++) { ffargs.Add("-map"); ffargs.Add($"0:a:{i}"); }
            ffargs.Add("-target"); ffargs.Add(opts.IsPal ? "pal-dvd" : "ntsc-dvd");
            ffargs.Add("-aspect"); ffargs.Add(aspect);
            // Cap near DVD-5 capacity; dvdauthor splits into 1 GB VOBs itself.
            ffargs.Add("-fs"); ffargs.Add("4290000000");
            ffargs.Add(tempMpg);

            var rr = ffmpeg.Run(ffargs, line =>
            {
                onLog?.Invoke(line);
                if (onProgress is not null && totalDuration > TimeSpan.Zero
                    && ParseFfmpegTime(line) is { } elapsed)
                {
                    double frac = Math.Clamp(elapsed.TotalSeconds / totalDuration.TotalSeconds, 0, 1);
                    onProgress(frac * 0.80);
                }
            });
            if (rr.ExitCode != 0 || !File.Exists(tempMpg))
                throw new InvalidOperationException($"ffmpeg transcode failed (exit {rr.ExitCode}).");
            onProgress?.Invoke(0.80);

            // --- Subtitles: spumux each text track into the stream. (0.80 .. 0.92)
            string authoredMpg = tempMpg;
            var subLangs = new List<string>();
            if (textSubs.Count > 0 && dvdauthor is not null)
            {
                var spumux = SpumuxRunner.Locate();
                if (spumux is null)
                {
                    onLog?.Invoke("spumux not found — subtitles skipped. (DVDStyler bundles it.)");
                }
                else
                {
                    onLog?.Invoke($"Muxing {textSubs.Count} subtitle track(s) via spumux ...");
                    for (int i = 0; i < textSubs.Count; i++)
                    {
                        var (subIdx, stream) = textSubs[i];
                        var lang = stream.Language ?? "und";
                        var srt  = Path.Combine(tempDir, $"sub{i}.srt");
                        var ex = ffmpeg.Run(new[] { "-y", "-i", opts.InputVideo,
                                                    "-map", $"0:s:{subIdx}", "-c:s", "srt", srt });
                        if (ex.ExitCode != 0 || !File.Exists(srt))
                        {
                            onLog?.Invoke($"  subtitle {i} ({lang}) — couldn't extract, skipped.");
                            continue;
                        }
                        var xml     = Path.Combine(tempDir, $"sub{i}.xml");
                        File.WriteAllText(xml, SpumuxRunner.BuildTextSubtitleXml(srt, opts.IsPal));
                        var nextMpg = Path.Combine(tempDir, $"title-s{i}.mpg");
                        try
                        {
                            spumux.Mux(authoredMpg, nextMpg, xml, subLangs.Count, opts.IsPal);
                            if (authoredMpg != tempMpg) { try { File.Delete(authoredMpg); } catch { } }
                            authoredMpg = nextMpg;
                            subLangs.Add(IsoLanguage.To2Letter(stream.Language));
                            onLog?.Invoke($"  subtitle {i} ({lang}) → subpicture stream {subLangs.Count - 1}");
                        }
                        catch (Exception sx)
                        {
                            onLog?.Invoke($"  subtitle {i} ({lang}) — spumux failed ({sx.Message}), skipped.");
                        }
                        onProgress?.Invoke(0.80 + 0.12 * (i + 1) / textSubs.Count);
                    }
                }
            }
            onProgress?.Invoke(0.92);

            // --- Author the DVD-Video folder. (0.92 .. 1.0)
            if (dvdauthor is not null)
            {
                onLog?.Invoke($"Authoring via dvdauthor ({dvdauthor.VersionLine}) ...");
                var spec = new DvdauthorRunner.DvdTitleSpec(
                    MpegFile:        authoredMpg,
                    IsPal:           opts.IsPal,
                    AspectRatio:     aspect,
                    ChapterStarts:   chapterStarts,
                    AudioLangs:      audioStreams
                        .Select(a => IsoLanguage.To2Letter(a.Language)).ToList(),
                    SubpictureLangs: subLangs);
                dvdauthor.Author(spec, opts.OutputFolder, onLog);
            }
            else
            {
                onLog?.Invoke("dvdauthor not found — writing skeleton IFOs (VLC-only).");
                var videoTs = Path.Combine(opts.OutputFolder, "VIDEO_TS");
                Directory.CreateDirectory(videoTs);
                File.Copy(authoredMpg, Path.Combine(videoTs, "VTS_01_1.VOB"), overwrite: true);
                var vmg = DvdIfoBuilder.BuildVmgIfo(numTitleSets: 1, providerId: label);
                var vts = DvdIfoBuilder.BuildVtsIfo();
                File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"), vmg);
                File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.BUP"), vmg);
                File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.IFO"), vts);
                File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.BUP"), vts);
            }

            // DVD-Video spec wants an AUDIO_TS folder even on a pure video disc.
            Directory.CreateDirectory(Path.Combine(opts.OutputFolder, "AUDIO_TS"));
            onProgress?.Invoke(1.0);

            return new AuthorResult(
                UsedDvdauthor:  dvdauthor is not null,
                AudioTracks:    audioStreams.Count,
                SubtitlesMuxed: subLangs.Count,
                Chapters:       chapterStarts.Count,
                Aspect:         aspect,
                OutputFolder:   opts.OutputFolder);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Subtitle codecs ffmpeg can re-emit as SRT text (so spumux can render
    /// them). Bitmap subtitle codecs (VobSub, PGS) can't and are skipped.
    /// </summary>
    public static bool IsTextSubtitle(string codecName) => codecName.ToLowerInvariant() switch
    {
        "dvd_subtitle" or "hdmv_pgs_subtitle" or "dvdsub" or "pgssub"
            or "xsub" or "dvb_subtitle" => false,
        _ => true,
    };

    /// <summary>Pick a DVD display aspect from the source video's pixel dimensions.</summary>
    public static string GuessAspect(FfprobeRunner.StreamInfo video)
    {
        if (video.Width is { } w and > 0 && video.Height is { } h and > 0)
            return (double)w / h > 1.5 ? "16:9" : "4:3";
        return "4:3";
    }

    private static readonly Regex FfmpegTimeRe =
        new(@"time=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Parse the <c>time=HH:MM:SS.ss</c> field out of an ffmpeg progress line.</summary>
    public static TimeSpan? ParseFfmpegTime(string ffmpegLine)
    {
        var m = FfmpegTimeRe.Match(ffmpegLine);
        if (!m.Success) return null;
        if (int.TryParse(m.Groups[1].Value, out int h)
            && int.TryParse(m.Groups[2].Value, out int min)
            && double.TryParse(m.Groups[3].Value,
                System.Globalization.CultureInfo.InvariantCulture, out double sec))
            return new TimeSpan(0, h, min, 0) + TimeSpan.FromSeconds(sec);
        return null;
    }
}
