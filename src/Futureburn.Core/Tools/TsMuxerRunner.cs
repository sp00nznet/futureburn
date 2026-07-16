using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Futureburn.Core.Tools;

// Thin wrapper around tsMuxeR.exe. Two operations:
//   Analyze(file)          -> the track list tsMuxeR sees (IDs, codecs, video
//                             resolution/framerate/level, audio channels, langs)
//   Mux(metaPath, output)  -> run a meta file to a BDMV folder or UDF 2.50 ISO
//
// The meta file is tsMuxeR's job description: a MUXOPT line plus one line per
// track. We build it in MkvBdPipeline; this class just runs it and reports the
// "NN.N% complete" progress tsMuxeR prints.

[SupportedOSPlatform("windows")]
public sealed class TsMuxerRunner
{
    public string ExePath { get; }
    public string VersionLine { get; }

    private TsMuxerRunner(string exePath, string versionLine)
    {
        ExePath = exePath;
        VersionLine = versionLine;
    }

    public static TsMuxerRunner? Locate()
    {
        var info = TsMuxerLocator.Locate();
        return info is null ? null : new TsMuxerRunner(info.Path, info.VersionLine);
    }

    public static TsMuxerRunner LocateOrThrow() =>
        Locate() ?? throw new InvalidOperationException(
            "tsMuxeR isn't installed. It builds the Blu-ray BDMV structure.\n" +
            "  Download tsMuxer-<ver>-win64.zip from\n" +
            "    https://github.com/justdan96/tsMuxer/releases\n" +
            "  and drop tsMuxeR.exe on your PATH (or beside futureburn.exe).\n" +
            "Then run `futureburn bd-author-info` to verify.");

    // One track as tsMuxeR reports it. StreamId is the tsMuxeR codec tag we
    // reuse verbatim in the meta (e.g. V_MPEG4/ISO/AVC, A_DTS, S_HDMV/PGS).
    public sealed record Track(
        int Id,
        string Kind,          // "video" | "audio" | "subtitle" | "other"
        string StreamType,    // tsMuxeR's human label: H.264, DTS, AC3, SRT, PGS ...
        string StreamId,      // tsMuxeR codec tag: V_MPEG4/ISO/AVC, A_AC3, ...
        int? Width,
        int? Height,
        double? FrameRate,
        int? Level10,         // H.264 level ×10 (High@4.1 -> 41), null if unknown
        int? Channels10,      // channel count ×10 (5.1 -> 51), null if unknown
        string? Lang);

    private static readonly Regex ReTrackId    = new(@"^Track ID:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex ReStreamType = new(@"^Stream type:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ReStreamId   = new(@"^Stream ID:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ReStreamInfo = new(@"^Stream info:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ReStreamLang = new(@"^Stream lang:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ReResolution = new(@"Resolution:\s*(\d+):(\d+)", RegexOptions.Compiled);
    private static readonly Regex ReFrameRate  = new(@"Frame rate:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex ReProfile    = new(@"@([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex ReChannels   = new(@"Channels:\s*([\d.]+)", RegexOptions.Compiled);

    /// <summary>Ask tsMuxeR to list the tracks in a media file.</summary>
    public IReadOnlyList<Track> Analyze(string inputFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(inputFile);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {ExePath}");
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        var tracks = new List<Track>();
        int? id = null; string type = "", sid = "", info = "", lang = "";

        void Flush()
        {
            if (id is null) return;
            int? w = null, h = null; double? fps = null; int? lvl = null, ch = null;
            var mRes = ReResolution.Match(info);
            if (mRes.Success) { w = int.Parse(mRes.Groups[1].Value); h = int.Parse(mRes.Groups[2].Value); }
            var mFps = ReFrameRate.Match(info);
            if (mFps.Success && double.TryParse(mFps.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var f)) fps = f;
            var mLvl = ReProfile.Match(info);   // "High@4.1" -> 4.1
            if (mLvl.Success && double.TryParse(mLvl.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var lv)) lvl = (int)Math.Round(lv * 10);
            var mCh = ReChannels.Match(info);
            if (mCh.Success && double.TryParse(mCh.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var c)) ch = (int)Math.Round(c * 10);

            tracks.Add(new Track(id.Value, KindOf(sid, type), type, sid, w, h, fps, lvl, ch,
                                 string.IsNullOrWhiteSpace(lang) ? null : lang));
            id = null; type = ""; sid = ""; info = ""; lang = "";
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var m = ReTrackId.Match(line);
            if (m.Success) { Flush(); id = int.Parse(m.Groups[1].Value); continue; }
            if (ReStreamType.Match(line) is { Success: true } mt) type = mt.Groups[1].Value.Trim();
            else if (ReStreamId.Match(line) is { Success: true } ms) sid = ms.Groups[1].Value.Trim();
            else if (ReStreamInfo.Match(line) is { Success: true } mi) info = mi.Groups[1].Value.Trim();
            else if (ReStreamLang.Match(line) is { Success: true } ml) lang = ml.Groups[1].Value.Trim();
        }
        Flush();
        return tracks;
    }

    private static string KindOf(string streamId, string streamType)
    {
        if (streamId.StartsWith("V_", StringComparison.OrdinalIgnoreCase)) return "video";
        if (streamId.StartsWith("A_", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (streamId.StartsWith("S_", StringComparison.OrdinalIgnoreCase)) return "subtitle";
        return "other";
    }

    private static readonly Regex RePct = new(@"([\d.]+)%\s+complete", RegexOptions.Compiled);

    /// <summary>
    /// Run a meta file. <paramref name="output"/> is a folder (BDMV) or a path
    /// ending in .iso (UDF 2.50 Blu-ray image). Progress 0..1 via onProgress.
    /// </summary>
    public void Mux(string metaPath, string output,
                    Action<string>? onLog = null, Action<double>? onProgress = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(metaPath);
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {ExePath}");

        var log = new System.Text.StringBuilder();
        void Handle(string? data)
        {
            if (data is null) return;
            log.AppendLine(data);
            var m = RePct.Match(data);
            if (m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
                onProgress?.Invoke(Math.Clamp(pct / 100.0, 0, 1));
            else
                onLog?.Invoke(data.Trim());
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived  += (_, e) => Handle(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"tsMuxeR exited {p.ExitCode}. Tail:\n" +
                string.Join("\n", log.ToString().Split('\n').TakeLast(8)));
    }
}
