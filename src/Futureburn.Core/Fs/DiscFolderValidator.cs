using System.Runtime.Versioning;

namespace Futureburn.Core.Fs;

// Inspect a folder on disk and identify what kind of disc it represents
// (DVD-Video, DVD-Audio, VCD, SVCD, Blu-ray, plain data) by checking for
// the standard well-known folder + file structures.
//
// This runs BEFORE any burn so we can warn the user if the folder doesn't
// actually have what a player expects. "It's almost a DVD-Video disc, but
// VIDEO_TS.IFO is missing" is much more useful than "your disc didn't play."
//
// We don't validate the binary contents of IFO/BUP/etc. files — just their
// presence and basic counterpart pairing. That's enough to catch most
// authoring mistakes (forgot to include backups, wrong folder name, etc.).

[SupportedOSPlatform("windows")]
public static class DiscFolderValidator
{
    public enum DiscType
    {
        Unknown,
        DataDisc,
        DvdVideo,
        DvdAudio,
        DvdAudioVideoHybrid,
        VideoCd,
        SuperVideoCd,
        BluRayMovie,
    }

    public sealed record Validation(
        DiscType Type,
        bool LooksWellFormed,
        IReadOnlyList<string> Findings,    // human-readable bullet list
        IReadOnlyList<string> Issues);     // problems that may make the disc unplayable

    public static Validation Validate(string folder)
    {
        if (!Directory.Exists(folder))
            return new Validation(DiscType.Unknown, false,
                Array.Empty<string>(),
                new[] { $"Folder not found: {folder}" });

        var root = new DirectoryInfo(folder);
        var entries = root.GetFileSystemInfos();
        var dirNames = entries
            .Where(e => (e.Attributes & FileAttributes.Directory) != 0)
            .Select(e => e.Name.ToUpperInvariant())
            .ToHashSet();

        bool dvdV   = dirNames.Contains("VIDEO_TS");
        bool dvdA   = dirNames.Contains("AUDIO_TS");
        bool vcd    = dirNames.Contains("VCD");
        bool svcd   = dirNames.Contains("SVCD");
        bool bdmv   = dirNames.Contains("BDMV");
        bool mpegav = dirNames.Contains("MPEGAV");

        // DVD-Video requires AUDIO_TS\ to exist (per spec) but it's empty.
        // We only count it as "DVD-Audio content" if it actually has IFO/AOB files.
        // Otherwise a pure DVD-Video disc looks like a hybrid which it isn't.
        bool dvdAHasContent = dvdA && Directory.Exists(Path.Combine(folder, "AUDIO_TS"))
            && Directory.GetFiles(Path.Combine(folder, "AUDIO_TS")).Length > 0;

        DiscType type;
        if      (bdmv)                       type = DiscType.BluRayMovie;
        else if (svcd)                       type = DiscType.SuperVideoCd;
        else if (vcd)                        type = DiscType.VideoCd;
        else if (dvdV && dvdAHasContent)     type = DiscType.DvdAudioVideoHybrid;
        else if (dvdV)                       type = DiscType.DvdVideo;
        else if (dvdAHasContent)             type = DiscType.DvdAudio;
        else                                 type = entries.Length == 0 ? DiscType.Unknown : DiscType.DataDisc;

        // Per-type structural checks.
        return type switch
        {
            DiscType.DvdVideo            => CheckDvdVideo(folder),
            DiscType.DvdAudio            => CheckDvdAudio(folder),
            DiscType.DvdAudioVideoHybrid => MergeValidations(CheckDvdVideo(folder), CheckDvdAudio(folder), DiscType.DvdAudioVideoHybrid),
            DiscType.VideoCd             => CheckVideoCd(folder, isSvcd: false),
            DiscType.SuperVideoCd        => CheckVideoCd(folder, isSvcd: true),
            DiscType.BluRayMovie         => CheckBluRay(folder),
            DiscType.DataDisc            => new Validation(type, true, new[] { $"Plain data disc with {entries.Length} top-level entries." }, Array.Empty<string>()),
            _                            => new Validation(type, false, Array.Empty<string>(), new[] { "Empty folder — nothing to burn." }),
        };
    }

    private static Validation CheckDvdVideo(string folder)
    {
        var ts = Path.Combine(folder, "VIDEO_TS");
        var atsPath = Path.Combine(folder, "AUDIO_TS");
        var findings = new List<string>();
        var issues   = new List<string>();

        // DVD-Video spec requires AUDIO_TS\ to exist (even empty).
        if (!Directory.Exists(atsPath))
            findings.Add("AUDIO_TS\\ folder not present — spec requires it (empty is fine).");
        else
            findings.Add("AUDIO_TS\\ folder present (empty is normal for pure DVD-Video).");

        bool hasIfo = File.Exists(Path.Combine(ts, "VIDEO_TS.IFO"));
        bool hasBup = File.Exists(Path.Combine(ts, "VIDEO_TS.BUP"));
        if (hasIfo) findings.Add("VIDEO_TS.IFO present (master info file).");
        else        issues  .Add("VIDEO_TS.IFO is missing — most DVD players will refuse this disc.");
        if (hasBup) findings.Add("VIDEO_TS.BUP present (backup of master info).");
        else        issues  .Add("VIDEO_TS.BUP missing — backup is required by the spec; some players reject the disc without it.");

        var vobs = Directory.Exists(ts)
            ? Directory.GetFiles(ts, "VTS_*.VOB")
            : Array.Empty<string>();
        if (vobs.Length > 0) findings.Add($"{vobs.Length} VOB file(s) present (the actual video).");
        else                 issues  .Add("No VTS_*.VOB files found — this disc has no video content.");

        return new Validation(DiscType.DvdVideo, issues.Count == 0, findings, issues);
    }

    private static Validation CheckDvdAudio(string folder)
    {
        var ts = Path.Combine(folder, "AUDIO_TS");
        var findings = new List<string>();
        var issues   = new List<string>();

        if (File.Exists(Path.Combine(ts, "AUDIO_TS.IFO"))) findings.Add("AUDIO_TS.IFO present (master info file).");
        else                                               issues  .Add("AUDIO_TS.IFO is missing.");
        if (File.Exists(Path.Combine(ts, "AUDIO_TS.BUP"))) findings.Add("AUDIO_TS.BUP present (backup of master info).");
        else                                               issues  .Add("AUDIO_TS.BUP missing — required by the DVD-Audio spec.");

        var aobs = Directory.Exists(ts) ? Directory.GetFiles(ts, "ATS_*.AOB") : Array.Empty<string>();
        if (aobs.Length > 0) findings.Add($"{aobs.Length} AOB file(s) present (Audio Objects = the actual hi-res audio).");
        else                 issues  .Add("No ATS_*.AOB files found — this disc has no audio content.");

        return new Validation(DiscType.DvdAudio, issues.Count == 0, findings, issues);
    }

    private static Validation CheckVideoCd(string folder, bool isSvcd)
    {
        var ts = Path.Combine(folder, isSvcd ? "SVCD" : "VCD");
        var findings = new List<string>();
        var issues   = new List<string>();

        // VCD requires INFO.VCD + ENTRIES.VCD; SVCD uses INFO.SVD + ENTRIES.SVD.
        var ext = isSvcd ? "SVD" : "VCD";
        if (File.Exists(Path.Combine(ts, $"INFO.{ext}")))    findings.Add($"INFO.{ext} present.");
        else                                                  issues  .Add($"INFO.{ext} missing — required.");
        if (File.Exists(Path.Combine(ts, $"ENTRIES.{ext}"))) findings.Add($"ENTRIES.{ext} present.");
        else                                                  issues  .Add($"ENTRIES.{ext} missing — required.");

        var mpegavDir = Path.Combine(folder, "MPEGAV");
        if (!Directory.Exists(mpegavDir))
        {
            issues.Add("MPEGAV/ folder missing — that's where the AVSEQ##.DAT video tracks live.");
        }
        else
        {
            var dats = Directory.GetFiles(mpegavDir, "AVSEQ*.DAT");
            if (dats.Length > 0) findings.Add($"{dats.Length} AVSEQ*.DAT video track(s) present.");
            else                 issues  .Add("MPEGAV/ has no AVSEQ*.DAT files — no video to play.");
        }

        // Note about Mode 2 sectors that we can't currently honor.
        findings.Add("(Note: spec-strict VCD wants Mode 2 Form 2 sectors for video tracks; " +
                     "futureburn currently writes Mode 1. Most modern players accept this; " +
                     "older standalone VCD players may not.)");

        var t = isSvcd ? DiscType.SuperVideoCd : DiscType.VideoCd;
        return new Validation(t, issues.Count == 0, findings, issues);
    }

    private static Validation CheckBluRay(string folder)
    {
        var bdmv = Path.Combine(folder, "BDMV");
        var findings = new List<string>();
        var issues   = new List<string>();

        if (File.Exists(Path.Combine(bdmv, "index.bdmv")))    findings.Add("BDMV/index.bdmv present.");
        else                                                   issues  .Add("BDMV/index.bdmv missing.");
        if (File.Exists(Path.Combine(bdmv, "MovieObject.bdmv"))) findings.Add("BDMV/MovieObject.bdmv present.");
        else                                                     issues  .Add("BDMV/MovieObject.bdmv missing.");

        var stream = Path.Combine(bdmv, "STREAM");
        if (Directory.Exists(stream))
        {
            var m2ts = Directory.GetFiles(stream, "*.m2ts");
            if (m2ts.Length > 0) findings.Add($"BDMV/STREAM/ has {m2ts.Length} .m2ts video file(s).");
            else                 issues  .Add("BDMV/STREAM/ exists but has no .m2ts files.");
        }
        else
        {
            issues.Add("BDMV/STREAM/ folder missing.");
        }

        return new Validation(DiscType.BluRayMovie, issues.Count == 0, findings, issues);
    }

    private static Validation MergeValidations(Validation a, Validation b, DiscType combined)
    {
        var f = a.Findings.Concat(b.Findings).ToList();
        var i = a.Issues.Concat(b.Issues).ToList();
        return new Validation(combined, i.Count == 0, f, i);
    }
}
