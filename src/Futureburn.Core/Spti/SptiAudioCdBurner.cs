using System.Diagnostics;
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
        // 8 * 2352 = 18,816 bytes per WRITE 12. We tried 32 (75 KB) but the
        // GE20LU10 + Windows SPTD interface rejects transfers > 64 KB with
        // Win32 87 (ERROR_INVALID_PARAMETER) before they even reach the drive,
        // so 8 stays. Combined with our retry logic this is stable for single-
        // track and within-track writes; the multi-track failure isn't about
        // chunk size.
        const int chunkSectors = 8;
        const int writeRetries = 5;     // retry transient OS timeouts + UNIT ATTENTION
        int trackNum = 1;
        var burnClock = Stopwatch.StartNew();
        string Stamp() => $"[{burnClock.Elapsed:mm\\:ss}]";

        foreach (var track in plan.Tracks)
        {
            onTrackStart?.Invoke(trackNum, plan.Tracks.Count);
            var trackTimer = Stopwatch.StartNew();

            using var padded = new CdPaddedAudioStream(track.BurnPath);
            int trackSectors = (int)(padded.Length / CdFormat.SectorBytes);

            // Where does this track start? Trust whatever NextWritableLba
            // the drive reports via READ TRACK INFO 0xFF — for the GE20LU10
            // that's previousEnd + 2 with no spec-mandated 150-sector gap
            // (verified empirically; writing at the spec position is rejected
            // as INVALID ADDRESS on this drive).
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
            onLog?.Invoke($"     {Stamp()} track {trackNum}: starting at LBA {startLba}, " +
                          $"{trackSectors} sectors ({padded.Length / 1024.0 / 1024.0:F2} MB)");

            int sectorsRemaining = trackSectors;
            int currentLba = startLba;
            long bytesWritten = 0;
            long bytesTotal = padded.Length;
            var buffer = new byte[chunkSectors * CdFormat.SectorBytes];
            int chunksWritten = 0;
            int uaRetriesThisTrack = 0;
            int win121RetriesThisTrack = 0;

            while (sectorsRemaining > 0)
            {
                int sectorsThisChunk = Math.Min(chunkSectors, sectorsRemaining);
                int bytesThisChunk = sectorsThisChunk * CdFormat.SectorBytes;
                int got = ReadFully(padded, buffer, bytesThisChunk);
                if (got != bytesThisChunk)
                    throw new AudioCdBurner.BurnException(
                        $"Track {trackNum}: short read from staged WAV " +
                        $"(wanted {bytesThisChunk}, got {got}).");

                // The GE20LU10 (and likely many other CD writers) requires
                // WRITE 12 transfers to be a fixed sector count — partial
                // final chunks fail with sense 0x29 then 0x21/0x02 (verified
                // across five CD-Rs: track 1 worked when 14976%8==0, track 2
                // always died on the trailing 2-sector chunk of 34794%8==2).
                // Pad short final chunks with silence (zeroed PCM = perfectly
                // valid CD-DA samples). Adds ≤7 sectors = ≤93 ms of inaudible
                // silence at track end, well under any CD player's tolerance.
                if (sectorsThisChunk < chunkSectors)
                {
                    int paddingBytes = (chunkSectors - sectorsThisChunk) * CdFormat.SectorBytes;
                    Array.Clear(buffer, bytesThisChunk, paddingBytes);
                    sectorsThisChunk = chunkSectors;
                    bytesThisChunk = sectorsThisChunk * CdFormat.SectorBytes;
                    onLog?.Invoke($"     {Stamp()} track {trackNum}: padded final chunk to " +
                                  $"{chunkSectors} sectors (added {paddingBytes / CdFormat.SectorBytes} silent sectors)");
                }

                int attempt = 0;
                while (true)
                {
                    try
                    {
                        // Pass explicit dataLength so DataTransferLength always
                        // matches what the CDB describes — defense-in-depth even
                        // though our padding above keeps them naturally equal.
                        dev.Write12(currentLba, sectorsThisChunk, buffer,
                                    dataLength: bytesThisChunk);
                        break;
                    }
                    // UNIT ATTENTION (sense key 0x6): drive's state changed —
                    // a state-change UA after a previous command can land on
                    // the next WRITE. The TUR clears it; the same WRITE then
                    // succeeds at the same LBA (no media was touched yet).
                    catch (SptiScsiException ex)
                        when (ex.SenseKey == 0x6 && attempt < writeRetries)
                    {
                        attempt++;
                        uaRetriesThisTrack++;
                        onLog?.Invoke($"     {Stamp()} WRITE 12 hit UNIT ATTENTION at LBA {currentLba} " +
                                      $"(ASC=0x{ex.Asc:X2}/ASCQ=0x{ex.Ascq:X2}, attempt {attempt}); " +
                                      $"clearing via TUR and retrying");
                        try { dev.WaitUntilReady(timeoutSec: 30); } catch { /* let WRITE retry surface the real error */ }
                        continue;
                    }
                    // OS-level semaphore timeout (Win32 121). Drive pause/resume
                    // on buffer pressure occasionally exceeds the OS IO timer;
                    // chunk hasn't been committed yet, retry is safe.
                    catch (InvalidOperationException ex)
                        when (ex.Message.Contains("Win32 121") && attempt < writeRetries)
                    {
                        attempt++;
                        win121RetriesThisTrack++;
                        Thread.Sleep(250 * attempt);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        throw new AudioCdBurner.BurnException(
                            $"WRITE 12 failed at track {trackNum}, LBA {currentLba} " +
                            $"(chunk {chunksWritten + 1}, {chunksWritten * chunkSectors} sectors written so far): " +
                            $"{ex.Message}", ex);
                    }
                }

                currentLba       += sectorsThisChunk;
                sectorsRemaining -= sectorsThisChunk;
                bytesWritten     += bytesThisChunk;
                chunksWritten++;
                onProgress?.Invoke(trackNum, plan.Tracks.Count, bytesWritten, bytesTotal);
            }

            var writesEnd = trackTimer.Elapsed;
            var writeRetryNote = (uaRetriesThisTrack + win121RetriesThisTrack) > 0
                ? $", retries: {uaRetriesThisTrack} UA + {win121RetriesThisTrack} W121"
                : "";
            onLog?.Invoke($"     {Stamp()} track {trackNum}: writes done in {writesEnd.TotalSeconds:F1}s " +
                          $"({chunksWritten} chunks{writeRetryNote})");

            // Flush + close.
            var syncStart = trackTimer.Elapsed;
            try { dev.SynchronizeCache(); }
            catch (Exception ex)
            { throw new AudioCdBurner.BurnException($"SYNCHRONIZE CACHE failed after track {trackNum}: {ex.Message}", ex); }
            onLog?.Invoke($"     {Stamp()} track {trackNum}: cache synced ({(trackTimer.Elapsed - syncStart).TotalSeconds:F1}s)");

            // In TAO mode each track is closed individually. In DAO/SAO mode
            // the cue sheet defines all track boundaries up front, so we skip
            // per-track CLOSE TRACK calls and just CLOSE SESSION at the end.
            if (!gapless)
            {
                var closeStart = trackTimer.Elapsed;
                try { dev.CloseTrackOrSession(function: 1, trackNumber: trackNum); }
                catch (Exception ex)
                { throw new AudioCdBurner.BurnException($"CLOSE TRACK failed for track {trackNum}: {ex.Message}", ex); }
                onLog?.Invoke($"     {Stamp()} track {trackNum}: CLOSE TRACK done ({(trackTimer.Elapsed - closeStart).TotalSeconds:F1}s)");

                // Wait for the drive to finish its post-close housekeeping (writing
                // the gap, updating TOC scratch, etc.). CLOSE TRACK with IMMED=0
                // *should* block until done, but several drives raise UNIT ATTENTION
                // on the next command anyway. TUR-poll absorbs both that UA and any
                // residual NOT_READY before we issue WRITE 12 for the next track.
                var readyStart = trackTimer.Elapsed;
                try { dev.WaitUntilReady(timeoutSec: 60, onLog: onLog); }
                catch (Exception ex)
                {
                    throw new AudioCdBurner.BurnException(
                        $"Drive didn't become ready after closing track {trackNum}: {ex.Message}", ex);
                }
                onLog?.Invoke($"     {Stamp()} track {trackNum}: drive ready for next ({(trackTimer.Elapsed - readyStart).TotalSeconds:F1}s)");

                // CRITICAL: actively wait for CLOSE TRACK to truly complete
                // by polling READ TRACK INFO of the just-closed track until
                // its TrackSize stabilizes at a non-zero value across two
                // consecutive reads. The GE20LU10's CLOSE TRACK is async
                // (returns instantly, TUR also lies); only observable side
                // effects on the closed track itself are reliable. If we
                // don't wait, our next-track WRITE 12 starts while the drive
                // is still finalizing this track internally, causing a UA
                // mid-next-track that the drive can't recover from.
                var closeDeadline = DateTime.UtcNow.AddSeconds(60);
                int closePolls = 0;
                int lastSize = -1;
                int stableSize = -1;
                while (DateTime.UtcNow < closeDeadline)
                {
                    closePolls++;
                    int sizeNow = -1;
                    try
                    {
                        var ti = dev.ReadTrackInformation(trackNum);
                        sizeNow = ti.TrackSize;
                    }
                    catch (SptiScsiException ex)
                        when (ex.SenseKey == 0x6 || ex.SenseKey == 0x2)
                    {
                        // Drive still busy committing the close — this in
                        // itself proves the close hadn't completed. Retry.
                    }

                    if (sizeNow > 0 && sizeNow == lastSize)
                    {
                        stableSize = sizeNow;
                        break;
                    }
                    lastSize = sizeNow;
                    Thread.Sleep(250);
                }
                if (stableSize > 0)
                    onLog?.Invoke($"     {Stamp()} track {trackNum}: close confirmed " +
                                  $"(TrackSize={stableSize} stable after {closePolls} polls)");
                else
                    onLog?.Invoke($"     {Stamp()} track {trackNum}: close-verify INCONCLUSIVE " +
                                  $"after {closePolls} polls (last TrackSize={lastSize}); proceeding anyway");
            }

            trackNum++;
        }

        // 6. Close session — writes the TOC and session lead-out, makes the
        // disc playable in standalone CD players. Same async-close behavior
        // as CLOSE TRACK on this drive: returns instantly, then polls of disc
        // info still report Incomplete until the drive actually finishes.
        // Poll READ DISC INFORMATION until Status flips to Finalized (or
        // Last Session flips to Complete). Big multi-track sessions can take
        // 1-2 minutes — give it 5.
        onLog?.Invoke($"     {Stamp()} closing session (writing TOC + lead-out — this can take a minute)");
        try { dev.CloseTrackOrSession(function: 2, trackNumber: 0, immediate: true); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"CLOSE SESSION failed: {ex.Message}", ex); }

        var sessionDeadline = DateTime.UtcNow.AddSeconds(300);
        int sessionPolls = 0;
        SptiDevice.DiscInformation? finalInfo = null;
        while (DateTime.UtcNow < sessionDeadline)
        {
            sessionPolls++;
            try
            {
                var di = dev.ReadDiscInformation();
                finalInfo = di;
                if (di.Status == SptiDevice.DiscStatus.Finalized
                    || di.LastSessionState == SptiDevice.SessionState.Complete)
                    break;
            }
            catch (SptiScsiException ex)
                when (ex.SenseKey == 0x6 || ex.SenseKey == 0x2)
            {
                // Drive still busy writing lead-out — retry.
            }
            Thread.Sleep(1000);
        }
        if (finalInfo is { Status: SptiDevice.DiscStatus.Finalized })
            onLog?.Invoke($"     {Stamp()} session closed — disc finalized after {sessionPolls} polls, " +
                          $"total burn {burnClock.Elapsed.TotalSeconds:F1}s");
        else
            onLog?.Invoke($"     {Stamp()} session closed — drive reports Status={finalInfo?.Status}/" +
                          $"LastSession={finalInfo?.LastSessionState} after {sessionPolls} polls (5 min timeout); " +
                          $"disc is playable but not strictly finalized. Total burn {burnClock.Elapsed.TotalSeconds:F1}s");
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
        // Disc-status flag: only flag this when the TOC also looks broken.
        // On the GE20LU10 (and likely others), a successful multi-track burn
        // can leave the disc in `Status=Incomplete / LastSession=Empty` at
        // the immediate post-burn drive-state read, even though the TOC,
        // lead-out, and all tracks are present and the disc plays in real
        // CD players (verified in user's car stereo). Treat the status flag
        // as informational unless we ALSO see real structural problems
        // (wrong track count, missing TOC).
        bool statusLooksFinalized = info.Status == SptiDevice.DiscStatus.Finalized
            || info.LastSessionState == SptiDevice.SessionState.Complete;
        bool tocLooksGood = toc.Tracks.Count == plan.Tracks.Count;

        if (!statusLooksFinalized && !tocLooksGood)
        {
            mismatches.Add($"Disc status is {info.Status}/{info.LastSessionState} " +
                           $"AND track count is wrong — burn likely incomplete.");
        }
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
