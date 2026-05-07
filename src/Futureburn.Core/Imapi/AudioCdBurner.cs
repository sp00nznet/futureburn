using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Futureburn.Core.Audio;

namespace Futureburn.Core.Imapi;

[SupportedOSPlatform("windows")]
public static class AudioCdBurner
{
    public sealed record TrackPlan(
        int Index,
        string SourcePath,        // path from the playlist
        string BurnPath,          // either same as SourcePath (if CD-format WAV) or a temp decoded WAV
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
        int ChosenSpeedSps,                    // sectors per second (1x = 75)
        IReadOnlyList<int> SupportedSpeedsSps,
        string TempDir)
    {
        public TimeSpan TotalDuration =>
            TimeSpan.FromSeconds((double)TotalSectors / CdFormat.SectorsPerSecond);

        public TimeSpan EstimatedBurnTime =>
            ChosenSpeedSps > 0
                ? TimeSpan.FromSeconds(TotalSectors / (double)ChosenSpeedSps)
                : TimeSpan.Zero;
    }

    public sealed class BurnException : Exception
    {
        public BurnException(string message) : base(message) { }
        public BurnException(string message, Exception inner) : base(message, inner) { }
    }

    // Audio CD speeds: 1x = 75 sectors/sec (one CD frame per 1/75 sec).
    // IDiscFormat2TrackAtOnce reports SupportedWriteSpeeds in sectors-per-second.
    public const int CdAudio1xSps = 75;
    public static int SpsToCdX(int sps) => sps / CdAudio1xSps;
    public static int CdXToSps(int x)   => x * CdAudio1xSps;

    public static BurnPlan Plan(
        OpticalDrive drive,
        Playlist playlist,
        string tempDir,
        int? requestedSpeedSps,
        bool allowNonBlank)
    {
        // 1. Disc must be CD-R or CD-RW.
        var profileCode = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
        if (profileCode == 0)
            throw new BurnException($"No disc in {drive.PrimaryMount ?? drive.UniqueId}.");
        if (profileCode != 0x0009 && profileCode != 0x000A)
        {
            var name = Mmc.LookupProfile(profileCode).Name;
            throw new BurnException(
                $"Loaded disc is {name}, not a CD-R or CD-RW. Audio CDs require CD-R or CD-RW media.");
        }

        // (No DiscInspector pre-check — DiscInspector can't read blank-CD-R details
        // either without PrepareMedia. We rely on QueryDisc's PrepareMedia attempt.)

        // 2. Decode (or pass through) each playlist track.
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
            // IMAPI minimum audio track is 4 seconds = 300 sectors.
            if (sectors < 300)
                throw new BurnException(
                    $"Track {idx} ({info.Duration.TotalSeconds:0.0}s) is shorter than the CD minimum (4 seconds).");

            trackPlans.Add(new TrackPlan(
                Index:          idx,
                SourcePath:     entry.Path,
                BurnPath:       burnPath,
                RequiredDecode: decoded,
                Duration:       info.Duration,
                Sectors:        sectors,
                Title:          entry.Title));

            totalSectors += sectors;
            idx++;
        }

        // 3. Probe the loaded disc via TAO.
        var (discFreeSec, discTotalSec, existingTracks, supportedSpeedsSps) = QueryDisc(drive);
        bool isBlank = existingTracks == 0;

        // 4. Capacity.
        if (totalSectors > discFreeSec)
        {
            throw new BurnException(
                $"Tracks need {totalSectors:N0} sectors ({totalSectors / (double)(CdFormat.SectorsPerSecond * 60):0.00} min) " +
                $"but disc has only {discFreeSec:N0} sectors free ({discFreeSec / (double)(CdFormat.SectorsPerSecond * 60):0.00} min).");
        }

        // 5. Speed selection.
        int chosenSpeed;
        if (requestedSpeedSps.HasValue)
        {
            if (supportedSpeedsSps.Count > 0 && !supportedSpeedsSps.Contains(requestedSpeedSps.Value))
            {
                var supportedList = string.Join(", ",
                    supportedSpeedsSps.Select(s => $"{SpsToCdX(s)}x"));
                throw new BurnException(
                    $"Speed {SpsToCdX(requestedSpeedSps.Value)}x not supported by drive for this disc. " +
                    $"Supported: {supportedList}");
            }
            chosenSpeed = requestedSpeedSps.Value;
        }
        else
        {
            chosenSpeed = supportedSpeedsSps.Count > 0 ? supportedSpeedsSps.Max() : 0;
        }

        // 6. Blank-ness.
        if (!isBlank && !allowNonBlank)
        {
            throw new BurnException(
                $"Disc has {existingTracks} existing track(s). Use --force to overwrite (CD-RW only).");
        }

        return new BurnPlan(
            Drive:                drive,
            Tracks:               trackPlans,
            TotalSectors:         totalSectors,
            DiscFreeSectors:      discFreeSec,
            DiscTotalSectors:     discTotalSec,
            DiscIsBlank:          isBlank,
            ChosenSpeedSps:       chosenSpeed,
            SupportedSpeedsSps:   supportedSpeedsSps,
            TempDir:              tempDir);
    }

    public static void ExecuteBurn(BurnPlan plan, Action<int, int>? onTrackStart = null)
    {
        IDiscRecorder2 recorder = (IDiscRecorder2)new MsftDiscRecorder2Class();
        try
        {
            recorder.InitializeDiscRecorder(plan.Drive.UniqueId);
            recorder.AcquireExclusiveAccess(force: true, clientName: "futureburn");

            try
            {
                IDiscFormat2TrackAtOnce tao = (IDiscFormat2TrackAtOnce)new MsftDiscFormat2TrackAtOnceClass();
                try
                {
                    tao.put_ClientName("futureburn");
                    tao.put_Recorder(recorder);

                    if (plan.ChosenSpeedSps > 0)
                        tao.SetWriteSpeed(plan.ChosenSpeedSps, rotationTypeIsPureCAV: false);

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

    // Open a TAO format object briefly to query disc state and supported speeds.
    private static (long freeSec, long totalSec, int existingTracks, IReadOnlyList<int> speedsSps)
        QueryDisc(OpticalDrive drive)
    {
        IDiscRecorder2 recorder = (IDiscRecorder2)new MsftDiscRecorder2Class();
        try
        {
            recorder.InitializeDiscRecorder(drive.UniqueId);

            try { recorder.AcquireExclusiveAccess(force: true, clientName: "futureburn"); }
            catch (COMException ex)
            {
                throw new BurnException(
                    $"Couldn't take exclusive access of {drive.PrimaryMount ?? drive.UniqueId} " +
                    $"(HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}", ex);
            }

            try
            {
                IDiscFormat2TrackAtOnce tao = (IDiscFormat2TrackAtOnce)new MsftDiscFormat2TrackAtOnceClass();
                try
                {
                    tao.put_ClientName("futureburn");

                    try { tao.put_Recorder(recorder); }
                    catch (COMException ex)
                    {
                        throw new BurnException(
                            $"Couldn't open the disc for audio writing (HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}. " +
                            "The disc may be finalized or unsuitable for audio.", ex);
                    }

                    // PrepareMedia is required before TotalSectorsOnMedia / FreeSectorsOnMedia /
                    // NumberOfExistingTracks / SupportedWriteSpeeds are readable. It reserves
                    // the drive but writes nothing — releasing the COM object below aborts
                    // the session cleanly (no AddAudioTrack means nothing committed).
                    try { tao.PrepareMedia(); }
                    catch (COMException ex)
                    {
                        var hr = (uint)ex.HResult;
                        var profile = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
                        var hint = profile == 0x0009
                            ? "The CD-R probably already has data on it (CD-R is write-once)."
                            : profile == 0x000A
                                ? "The CD-RW may need to be erased first."
                                : "The disc may be finalized or unsuitable for audio writing.";
                        throw new BurnException(
                            $"PrepareMedia failed (HRESULT 0x{hr:X8}): {ex.Message}\n  {hint}", ex);
                    }

                    long totalSec = tao.get_TotalSectorsOnMedia();
                    long freeSec  = tao.get_FreeSectorsOnMedia();
                    int  existing = tao.get_NumberOfExistingTracks();

                    int[] speedsArr;
                    try { speedsArr = tao.get_SupportedWriteSpeeds() ?? Array.Empty<int>(); }
                    catch { speedsArr = Array.Empty<int>(); }

                    var speeds = speedsArr.Distinct().OrderBy(s => s).ToArray();

                    return (freeSec, totalSec, existing, speeds);
                }
                finally { Marshal.FinalReleaseComObject(tao); }
            }
            finally
            {
                try { recorder.ReleaseExclusiveAccess(); } catch { /* best-effort */ }
            }
        }
        finally { Marshal.FinalReleaseComObject(recorder); }
    }
}
