using System.Runtime.Versioning;
using Futureburn.Core.Audio;
using Futureburn.Core.Imapi;

namespace Futureburn.Core.Spti;

// Audio CD burn engine via raw SCSI MMC commands (no IMAPI involvement at all).
// Used when both IMAPI v2 and IMAPI v1 fail on the user's drive — which turned
// out to be the case for the LG GE20LU10 / FE06 on Windows 11.
//
// Flow (TAO mode, 2-second standard Red Book gaps between tracks):
//   1. Open drive with write access via SptiDevice
//   2. INQUIRY + READ DISC INFORMATION as sanity checks (drive responds, disc blank)
//   3. MODE SELECT 10 with Write Parameters Mode Page 0x05:
//        WriteType = TAO, TrackMode = Audio, DataBlockType = raw 2352, SessionFormat = CD-DA
//   4. For each track:
//        a. CdPaddedAudioStream wraps the WAV file (strips header, pads to 2352 boundary)
//        b. READ TRACK INFORMATION (track=0xFF, the invisible/incomplete track)
//           tells us NextWritableLba — where the drive will accept the next WRITE 12
//        c. WRITE 12 in chunks of 32 sectors (~75 KB) until the track is fully sent
//        d. SYNCHRONIZE CACHE flushes the drive's internal buffer to the disc
//        e. CLOSE TRACK (function 1, this track number) seals the track
//   5. CLOSE SESSION (function 2, track 0) finalizes the disc — TOC and lead-out written
//
// The disc that comes out of this should be Disc Status = Finalized, Last Session = Complete
// per READ DISC INFORMATION, and play in any standalone CD player.

[SupportedOSPlatform("windows")]
public static class SptiAudioCdBurner
{
    public sealed record SptiBurnPlan(
        OpticalDrive Drive,
        IReadOnlyList<AudioCdBurner.TrackPlan> Tracks,
        int TotalSectors,
        string TempDir);

    public static SptiBurnPlan Plan(
        OpticalDrive drive,
        Audio.Playlist playlist,
        string tempDir)
    {
        // Same per-track decode + size validation as the other engines.
        Directory.CreateDirectory(tempDir);
        var trackPlans = new List<AudioCdBurner.TrackPlan>();
        int totalSectors = 0;
        int idx = 1;

        foreach (var entry in playlist.Entries)
        {
            if (!File.Exists(entry.Path))
                throw new AudioCdBurner.BurnException($"Track {idx} not found on disk: {entry.Path}");

            AudioInfo info = AudioDecoder.Probe(entry.Path);
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

            int sectors = (int)CdFormat.SectorsForDuration(info.Duration);
            if (sectors < 300)
                throw new AudioCdBurner.BurnException(
                    $"Track {idx} ({info.Duration.TotalSeconds:0.0}s) is shorter than the CD minimum (4 seconds).");

            trackPlans.Add(new AudioCdBurner.TrackPlan(
                Index: idx, SourcePath: entry.Path, BurnPath: burnPath,
                RequiredDecode: decoded, Duration: info.Duration,
                Sectors: sectors, Title: entry.Title));
            totalSectors += sectors;
            idx++;
        }

        return new SptiBurnPlan(drive, trackPlans, totalSectors, tempDir);
    }

    public static void ExecuteBurn(SptiBurnPlan plan,
                                   int? requestedCdSpeedX = null,
                                   bool gapless = false,
                                   Action<int, int>? onTrackStart = null,
                                   Action<int, int, long, long>? onProgress = null,
                                   Action<string>? onLog = null)
    {
        var mount = plan.Drive.PrimaryMount
            ?? throw new AudioCdBurner.BurnException("Drive has no mount point — can't open via SPTI.");
        char letter = mount[0];

        using var dev = SptiDevice.OpenDriveLetter(letter);

        // 1. Sanity: drive answers SCSI?
        var inq = dev.Inquiry();
        // (We don't check vendor — we just want to confirm INQUIRY works.)

        // 2. Sanity: disc is blank.
        var info = dev.ReadDiscInformation();
        if (info.Status != SptiDevice.DiscStatus.Empty)
            throw new AudioCdBurner.BurnException(
                $"Disc isn't blank (status: {info.Status}). SPTI burn requires a virgin CD-R.");

        // 3. Set CD speed if requested. Old USB writers (LG GE20LU10) are way more
        // reliable at slow speeds — they can hit MEDIUM_ERROR ECC failures at full
        // negotiated speed even on virgin discs. 4x or 8x is a sane safety pick.
        if (requestedCdSpeedX is { } x && x > 0)
        {
            // Audio CD 1x raw rate ≈ 176 KB/s (2352 bytes * 75 sectors/sec).
            int kbps = x * 176;
            try { dev.SetCdSpeed(readSpeedKbps: 0xFFFF, writeSpeedKbps: kbps); }
            catch (Exception ex)
            {
                throw new AudioCdBurner.BurnException(
                    $"SET CD SPEED to {x}x ({kbps} KB/s) failed: {ex.Message}", ex);
            }
        }

        // 4. Set Write Parameters Mode Page for the chosen write mode + CD-DA + raw sectors.
        var writeMode = gapless ? SptiDevice.CdAudioWriteMode.SessionAtOnce
                                : SptiDevice.CdAudioWriteMode.TrackAtOnce;
        try { dev.ConfigureForAudio(writeMode); }
        catch (Exception ex)
        {
            throw new AudioCdBurner.BurnException(
                $"MODE SELECT 10 ({writeMode}) failed: {ex.Message}", ex);
        }

        // For gapless DAO: send the cue sheet describing the disc layout BEFORE
        // any WRITE 12 calls. The drive uses it to know where each track begins.
        if (gapless)
        {
            onLog?.Invoke("Building cue sheet for gapless DAO burn ...");
            var cueTracks = plan.Tracks.Select(t => new SptiCueSheet.Track(t.Sectors)).ToArray();
            var cueSheet  = SptiCueSheet.BuildAudioCd(cueTracks, gapless: true);
            onLog?.Invoke(SptiCueSheet.Dump(cueSheet));
            try { dev.SendCueSheet(cueSheet); }
            catch (Exception ex)
            {
                throw new AudioCdBurner.BurnException(
                    $"SEND CUE SHEET failed: {ex.Message}\n  " +
                    "(Gapless DAO mode is experimental — the cue sheet bytes are above. " +
                    "If the drive rejected them, the binary layout is probably wrong.)", ex);
            }
        }

        // 5. Burn each track.
        // 8 * 2352 = 18,816 bytes per WRITE 12 — very conservative, leaves lots
        // of headroom for the OS-level semaphore timeout. With BUFE enabled the
        // throughput cost of smaller chunks is negligible (drive pause/resume
        // dominates either way).
        const int chunkSectors = 8;
        const int writeRetries = 3;     // retry transient OS timeouts (Win32 121)
        int trackNum = 1;
        foreach (var track in plan.Tracks)
        {
            onTrackStart?.Invoke(trackNum, plan.Tracks.Count);

            using var padded = new CdPaddedAudioStream(track.BurnPath);
            int trackSectors = (int)(padded.Length / CdFormat.SectorBytes);

            // Find where this track lives. For the very first track the drive
            // accepts WRITE 12 at LBA 0; for later tracks we read the invisible-
            // track's NextWritableLba (the drive auto-advances past the gap on CLOSE TRACK).
            int startLba;
            try
            {
                var ti = dev.ReadTrackInformation(0xFF);
                startLba = ti.NextWritableLba;
            }
            catch
            {
                // Some drives reject READ TRACK INFORMATION on truly blank
                // discs. For the first track that just means LBA 0.
                startLba = 0;
            }

            int sectorsRemaining = trackSectors;
            int currentLba = startLba;
            long bytesWritten = 0;
            long bytesTotal = padded.Length;
            var buffer = new byte[chunkSectors * CdFormat.SectorBytes];

            while (sectorsRemaining > 0)
            {
                int sectorsThisChunk = Math.Min(chunkSectors, sectorsRemaining);
                int bytesThisChunk = sectorsThisChunk * CdFormat.SectorBytes;
                int got = ReadFully(padded, buffer, bytesThisChunk);
                if (got != bytesThisChunk)
                    throw new AudioCdBurner.BurnException(
                        $"Track {trackNum}: short read from staged WAV " +
                        $"(wanted {bytesThisChunk}, got {got}).");

                // Retry on transient OS-level semaphore timeouts (Win32 121).
                // The drive's pause/resume on buffer pressure occasionally exceeds
                // the OS IO timer; the chunk hasn't been committed to media so a
                // retry with the same data and same LBA is safe.
                int attempt = 0;
                while (true)
                {
                    try
                    {
                        dev.Write12(currentLba, sectorsThisChunk, buffer);
                        break;
                    }
                    catch (InvalidOperationException ex)
                        when (ex.Message.Contains("Win32 121") && attempt < writeRetries)
                    {
                        attempt++;
                        Thread.Sleep(250 * attempt);  // brief backoff
                        continue;
                    }
                    catch (Exception ex)
                    {
                        throw new AudioCdBurner.BurnException(
                            $"WRITE 12 failed at track {trackNum}, LBA {currentLba}: {ex.Message}", ex);
                    }
                }

                currentLba       += sectorsThisChunk;
                sectorsRemaining -= sectorsThisChunk;
                bytesWritten     += bytesThisChunk;
                onProgress?.Invoke(trackNum, plan.Tracks.Count, bytesWritten, bytesTotal);
            }

            // Flush + close.
            try { dev.SynchronizeCache(); }
            catch (Exception ex)
            { throw new AudioCdBurner.BurnException($"SYNCHRONIZE CACHE failed after track {trackNum}: {ex.Message}", ex); }

            // In TAO mode each track is closed individually. In DAO/SAO mode
            // the cue sheet defines all track boundaries up front, so we skip
            // per-track CLOSE TRACK calls and just CLOSE SESSION at the end.
            if (!gapless)
            {
                try { dev.CloseTrackOrSession(function: 1, trackNumber: trackNum); }
                catch (Exception ex)
                { throw new AudioCdBurner.BurnException($"CLOSE TRACK failed for track {trackNum}: {ex.Message}", ex); }
            }

            trackNum++;
        }

        // 6. Close session — finalizes the disc, writes the TOC and lead-out.
        try { dev.CloseTrackOrSession(function: 2, trackNumber: 0); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"CLOSE SESSION failed: {ex.Message}", ex); }
    }

    public sealed record VerificationResult(
        bool Passed,
        SptiDevice.DiscStatus DiscStatus,
        SptiDevice.SessionState SessionState,
        int TrackCount,
        int ExpectedTrackCount,
        IReadOnlyList<string> Mismatches);

    /// <summary>
    /// Post-burn sanity check: open the drive again, read the disc info + TOC,
    /// and compare against what the plan asked for. Tolerant of small per-track
    /// duration differences (rounding, padding) — flags only structural problems.
    /// </summary>
    public static VerificationResult Verify(SptiBurnPlan plan)
    {
        var mount = plan.Drive.PrimaryMount
            ?? throw new InvalidOperationException("Drive has no mount point.");
        char letter = mount[0];

        using var dev = SptiDevice.OpenDriveLetter(letter);
        var info = dev.ReadDiscInformation();
        var toc  = dev.ReadToc();

        var mismatches = new List<string>();
        if (info.Status != SptiDevice.DiscStatus.Finalized)
            mismatches.Add($"Disc status is {info.Status}, expected Finalized.");
        if (info.LastSessionState != SptiDevice.SessionState.Complete)
            mismatches.Add($"Last session is {info.LastSessionState}, expected Complete.");
        if (toc.Tracks.Count != plan.Tracks.Count)
            mismatches.Add($"Disc has {toc.Tracks.Count} tracks, plan expected {plan.Tracks.Count}.");

        // Compare each track's duration to plan, allowing a small tolerance for
        // sector-boundary padding (tracks can be 1-2 seconds longer than source).
        int compareCount = Math.Min(toc.Tracks.Count, plan.Tracks.Count);
        for (int i = 0; i < compareCount; i++)
        {
            var planSec = plan.Tracks[i].Duration.TotalSeconds;
            var diskSec = toc.Tracks[i].Duration.TotalSeconds;
            var deltaSec = Math.Abs(planSec - diskSec);
            if (deltaSec > 3)  // > 3 sec off is suspicious
                mismatches.Add(
                    $"Track {i + 1} duration mismatch: planned {planSec:0.0}s, disc {diskSec:0.0}s.");
        }

        return new VerificationResult(
            Passed:             mismatches.Count == 0,
            DiscStatus:         info.Status,
            SessionState:       info.LastSessionState,
            TrackCount:         toc.Tracks.Count,
            ExpectedTrackCount: plan.Tracks.Count,
            Mismatches:         mismatches);
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
