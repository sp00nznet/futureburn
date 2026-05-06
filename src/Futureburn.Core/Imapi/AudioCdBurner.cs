using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Futureburn.Core.Audio;

namespace Futureburn.Core.Imapi;

[SupportedOSPlatform("windows")]
public static class AudioCdBurner
{
    public sealed record TrackPlan(
        int Index,
        string SourcePath,        // path from playlist
        string BurnPath,          // either same as SourcePath (if CD-format WAV) or temp decoded WAV
        bool RequiredDecode,      // true if we wrote a temp file
        TimeSpan Duration,
        long Sectors,
        string? Title);

    public sealed record BurnPlan(
        OpticalDrive Drive,
        IReadOnlyList<TrackPlan> Tracks,
        long TotalSectors,
        long DiscFreeSectors,
        long DiscTotalSectors,
        bool DiscIsBlank,
        int ChosenSpeedKbps,
        IReadOnlyList<int> SupportedSpeedsKbps,
        string TempDir)
    {
        public TimeSpan TotalDuration =>
            TimeSpan.FromSeconds((double)TotalSectors / CdFormat.SectorsPerSecond);

        public TimeSpan EstimatedBurnTime =>
            ChosenSpeedKbps > 0
                ? TimeSpan.FromSeconds(TotalSectors / (double)(CdFormat.SectorsPerSecond * KbpsToCdX(ChosenSpeedKbps)))
                : TimeSpan.Zero;
    }

    public sealed class BurnException : Exception
    {
        public BurnException(string message) : base(message) { }
        public BurnException(string message, Exception inner) : base(message, inner) { }
    }

    // Audio CD 1x speed = 150 KB/s (or 176.4 KB/s if you include the 2352-byte raw frames,
    // but IMAPI2 reports speeds in terms of 150 KB/s so we use that).
    public const int CdAudio1xKbps = 150;
    public static int KbpsToCdX(int kbps) => kbps / CdAudio1xKbps;
    public static int CdXToKbps(int x) => x * CdAudio1xKbps;

    /// <summary>
    /// Validate the burn request and prepare a plan. Doesn't burn anything
    /// (but may write decoded temp WAVs into <paramref name="tempDir"/>).
    /// </summary>
    public static BurnPlan Plan(
        OpticalDrive drive,
        Playlist playlist,
        string tempDir,
        int? requestedSpeedKbps,
        bool allowNonBlank)
    {
        // 1. The disc must be a CD-R or CD-RW. Audio CD format won't work on DVD/BD.
        var profileCode = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
        if (profileCode == 0)
            throw new BurnException($"No disc in {drive.PrimaryMount ?? drive.UniqueId}.");
        if (profileCode != 0x0009 && profileCode != 0x000A)
        {
            var name = Mmc.LookupProfile(profileCode).Name;
            throw new BurnException(
                $"Loaded disc is {name}, not a CD-R or CD-RW. Audio CDs require CD-R or CD-RW media.");
        }

        // 1a. Quick blank-ness pre-check via MsftDiscFormat2Data (DiscInspector).
        // If even the data-format won't read the disc's capacity, IMAPI's TAO path
        // will also fail later with a cryptic "mode page not present" SCSI error.
        // Surfacing this clearly here saves the user from puzzling over that.
        try
        {
            var quickLook = DiscInspector.InspectDrive(drive);
            if (!quickLook.HasFormatDetails)
            {
                var media = quickLook.MediaTypeName;
                throw new BurnException(
                    $"The {media} in {drive.PrimaryMount ?? drive.UniqueId} doesn't look fresh. " +
                    "Both the data and TAO format objects refuse to read its capacity, which is the " +
                    "symptom of an already-written CD-R, a finalized disc, or an existing audio CD. " +
                    (profileCode == 0x0009
                        ? "CD-R is write-once — used discs can't be re-burned. Insert a blank CD-R."
                        : "Try erasing the CD-RW first (erase command coming in v0.0.7)."));
            }
        }
        catch (DiscInspector.NoMediaException ex) { throw new BurnException(ex.Message, ex); }
        // Other exceptions from InspectDrive: let them surface unchanged.

        // 2. For each track: locate, probe, decode-if-needed.
        Directory.CreateDirectory(tempDir);
        var trackPlans = new List<TrackPlan>();
        long totalSectors = 0;
        int idx = 1;

        foreach (var entry in playlist.Entries)
        {
            if (!File.Exists(entry.Path))
                throw new BurnException($"Track {idx} not found on disk: {entry.Path}");

            AudioInfo info;
            try { info = AudioDecoder.Probe(entry.Path); }
            catch (Exception ex)
            { throw new BurnException($"Track {idx} couldn't be probed ({entry.Path}): {ex.Message}", ex); }

            string burnPath;
            bool decoded;
            if (info.IsCdFormat &&
                Path.GetExtension(entry.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                burnPath = entry.Path;
                decoded  = false;
            }
            else
            {
                burnPath = Path.Combine(tempDir, $"track-{idx:00}.wav");
                AudioDecoder.DecodeToCdWav(entry.Path, burnPath);
                decoded = true;
                info    = AudioDecoder.Probe(burnPath);
            }

            var sectors = CdFormat.SectorsForDuration(info.Duration);
            // IMAPI minimum audio track is 4 seconds = 300 sectors. Refuse tiny ones.
            if (sectors < 300)
                throw new BurnException(
                    $"Track {idx} ({info.Duration.TotalSeconds:0.0}s) is shorter than the CD minimum (4 seconds).");

            trackPlans.Add(new TrackPlan(
                Index:           idx,
                SourcePath:      entry.Path,
                BurnPath:        burnPath,
                RequiredDecode:  decoded,
                Duration:        info.Duration,
                Sectors:         sectors,
                Title:           entry.Title));

            totalSectors += sectors;
            idx++;
        }

        // 3. Probe the loaded disc via TAO format object.
        var (discFreeSec, discTotalSec, existingTracks, supportedSpeedsKbps) = QueryDisc(drive);
        bool isBlank = existingTracks == 0;

        // 4. Validate capacity.
        if (totalSectors > discFreeSec)
        {
            throw new BurnException(
                $"Tracks need {totalSectors:N0} sectors ({totalSectors / (double)(CdFormat.SectorsPerSecond * 60):0.00} min) " +
                $"but disc has only {discFreeSec:N0} sectors free ({discFreeSec / (double)(CdFormat.SectorsPerSecond * 60):0.00} min).");
        }

        // 5. Validate speed selection.
        int chosenSpeed;
        if (requestedSpeedKbps.HasValue)
        {
            if (supportedSpeedsKbps.Count > 0 && !supportedSpeedsKbps.Contains(requestedSpeedKbps.Value))
            {
                var supportedList = string.Join(", ",
                    supportedSpeedsKbps.Select(s => $"{KbpsToCdX(s)}x ({s} KB/s)"));
                throw new BurnException(
                    $"Speed {KbpsToCdX(requestedSpeedKbps.Value)}x ({requestedSpeedKbps.Value} KB/s) " +
                    $"not supported by drive for this disc.\n  Supported: {supportedList}");
            }
            chosenSpeed = requestedSpeedKbps.Value;
        }
        else
        {
            chosenSpeed = supportedSpeedsKbps.Count > 0 ? supportedSpeedsKbps.Max() : 0;
        }

        // 6. Validate blank-ness (unless --force).
        if (!isBlank && !allowNonBlank)
        {
            throw new BurnException(
                $"Disc has {existingTracks} existing track(s). " +
                "Use --force to overwrite (only works on CD-RW; CD-R can't be erased).");
        }

        return new BurnPlan(
            Drive:                drive,
            Tracks:               trackPlans,
            TotalSectors:         totalSectors,
            DiscFreeSectors:      discFreeSec,
            DiscTotalSectors:     discTotalSec,
            DiscIsBlank:          isBlank,
            ChosenSpeedKbps:      chosenSpeed,
            SupportedSpeedsKbps:  supportedSpeedsKbps,
            TempDir:              tempDir);
    }

    /// <summary>
    /// Burn the planned tracks. This blocks per-track for the duration of
    /// each track's write. Caller is expected to have confirmed the action.
    /// </summary>
    public static void ExecuteBurn(BurnPlan plan, Action<int, int>? onTrackStart = null)
    {
        var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
            ?? throw new BurnException("IMAPI2 recorder COM class missing.");
        var taoType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2TrackAtOnce")
            ?? throw new BurnException("IMAPI2 TAO format COM class missing.");

        dynamic recorder = Activator.CreateInstance(recorderType)!;
        try
        {
            recorder.InitializeDiscRecorder(plan.Drive.UniqueId);
            recorder.AcquireExclusiveAccess(false, "futureburn");

            try
            {
                dynamic tao = Activator.CreateInstance(taoType)!;
                try
                {
                    tao.ClientName          = "futureburn";
                    tao.Recorder            = recorder;
                    tao.RequestedWriteSpeed = plan.ChosenSpeedKbps;

                    tao.PrepareMedia();

                    int n = 1;
                    foreach (var track in plan.Tracks)
                    {
                        onTrackStart?.Invoke(n, plan.Tracks.Count);
                        using var padded  = new CdPaddedAudioStream(track.BurnPath);
                        using var iStream = new ManagedIStream(padded);
                        tao.AddAudioTrack(iStream);
                        n++;
                    }

                    tao.ReleaseMedia();
                }
                finally { Marshal.FinalReleaseComObject(tao); }
            }
            finally
            {
                try { recorder.ReleaseExclusiveAccess(); } catch { /* best-effort */ }
            }
        }
        catch (COMException ex)
        {
            throw new BurnException(
                $"IMAPI2 burn failed (HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}", ex);
        }
        finally
        {
            Marshal.FinalReleaseComObject(recorder);
        }
    }

    // Open the TAO format object briefly to query disc state and supported speeds.
    private static (long freeSec, long totalSec, int existingTracks, IReadOnlyList<int> speedsKbps)
        QueryDisc(OpticalDrive drive)
    {
        var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
            ?? throw new BurnException("IMAPI2 recorder COM class missing.");
        var taoType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2TrackAtOnce")
            ?? throw new BurnException("IMAPI2 TAO format COM class missing.");

        dynamic recorder = Activator.CreateInstance(recorderType)!;
        try
        {
            recorder.InitializeDiscRecorder(drive.UniqueId);
            dynamic tao = Activator.CreateInstance(taoType)!;
            try
            {
                tao.ClientName = "futureburn";
                try { tao.Recorder = recorder; }
                catch (COMException ex)
                {
                    throw new BurnException(
                        $"Couldn't open the disc for audio writing (HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}. " +
                        "The disc may be already finalized or unsuitable for audio.", ex);
                }

                // PrepareMedia is required before TotalSectorsOnMedia / FreeSectorsOnMedia /
                // NumberOfExistingTracks / SupportedWriteSpeeds are readable. It reserves the
                // drive but writes nothing. Releasing the COM object below aborts the
                // session cleanly — no AddAudioTrack means nothing committed to the disc.
                try { tao.PrepareMedia(); }
                catch (COMException ex)
                {
                    throw new BurnException(
                        $"PrepareMedia failed (HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}. " +
                        "The disc is probably finalized or otherwise unsuitable.", ex);
                }

                long totalSec       = Convert.ToInt32(tao.TotalSectorsOnMedia);
                long freeSec        = Convert.ToInt32(tao.FreeSectorsOnMedia);
                int  existing       = Convert.ToInt32(tao.NumberOfExistingTracks);

                var speeds = new List<int>();
                try
                {
                    var arr = (Array?)tao.SupportedWriteSpeeds;
                    if (arr is not null)
                        for (int i = 0; i < arr.Length; i++)
                            speeds.Add(Convert.ToInt32(arr.GetValue(i)));
                    speeds = speeds.Distinct().OrderBy(s => s).ToList();
                }
                catch { /* leave empty if not available */ }

                return (freeSec, totalSec, existing, speeds);
            }
            finally { Marshal.FinalReleaseComObject(tao); }
        }
        finally { Marshal.FinalReleaseComObject(recorder); }
    }
}
