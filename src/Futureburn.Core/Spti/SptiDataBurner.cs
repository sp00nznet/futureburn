using System.Runtime.Versioning;
using Futureburn.Core.Imapi;

namespace Futureburn.Core.Spti;

// Burn a pre-built ISO image to a blank CD-R or DVD-R via raw SCSI MMC.
//
// "Pre-built" = the bytes are already a valid disc image (ISO 9660, Joliet,
// UDF, DVD-Video VIDEO_TS, bootable CD, whatever). We treat the ISO as an
// opaque sequence of 2048-byte data sectors and pipe them to the drive.
// Building the file system from a folder of files is a SEPARATE problem —
// for that we'd use IMAPI's MsftFileSystemImage or write our own UDF
// builder. This burner doesn't author anything.
//
// Same general flow as SptiAudioCdBurner:
//   1. Open drive via SPTI
//   2. INQUIRY + READ DISC INFORMATION sanity
//   3. SET CD SPEED (optional)
//   4. MODE SELECT 10 with the right Write Parameters page (CD vs DVD)
//   5. WRITE 12 the ISO sectors in chunks
//   6. SYNCHRONIZE CACHE → CLOSE TRACK → CLOSE SESSION
//
// One disc = one image. No multi-session. No track ordering decisions.
// Way simpler than audio CDs.

[SupportedOSPlatform("windows")]
public static class SptiDataBurner
{
    public const int DataSectorBytes = 2048;

    public sealed record DataBurnPlan(
        OpticalDrive Drive,
        string ImagePath,
        // For BIN/CUE inputs this is the path to the .bin (resolved from the
        // .cue's FILE directive). For ISOs it's the path passed in.
        string ActualBinaryPath,
        long ImageBytes,
        long ImageSectors,
        bool IsDvd,
        bool IsBinCue);

    public static DataBurnPlan Plan(OpticalDrive drive, string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new AudioCdBurner.BurnException($"Image file not found: {imagePath}");

        // BIN/CUE detection: if the user hands us a .cue we parse it and use
        // its referenced .bin. We currently only support single-data-track
        // BIN/CUE pairs (ISO-equivalent). Audio BIN/CUE comes later.
        bool isBinCue = imagePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);
        string actualBin = imagePath;
        long imageBytes;
        long imageSectors;
        if (isBinCue)
        {
            var cue = Image.CueSheetParser.Parse(imagePath);
            if (!cue.IsSingleDataTrack)
                throw new AudioCdBurner.BurnException(
                    "BIN/CUE burning currently supports single-data-track sheets only " +
                    "(MODE1/2048 or MODE1/2352). Audio CD BIN/CUE is on the roadmap.");
            var t = cue.Tracks[0];
            actualBin   = cue.BinFile;
            if (!File.Exists(actualBin))
                throw new AudioCdBurner.BurnException(
                    $"BIN file referenced by cue sheet not found: {actualBin}");
            // Logical sectors = bin length divided by per-sector size, but the
            // burnable payload is always 2048 bytes per logical sector.
            var binLen = new FileInfo(actualBin).Length;
            imageSectors = binLen / t.SectorBytes;
            imageBytes   = imageSectors * 2048;
        }
        else
        {
            var fi = new FileInfo(imagePath);
            imageBytes   = fi.Length;
            imageSectors = (imageBytes + DataSectorBytes - 1) / DataSectorBytes;
        }

        var profileCode = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
        if (profileCode == 0)
            throw new AudioCdBurner.BurnException(
                $"No disc in {drive.PrimaryMount ?? drive.UniqueId}.");

        bool isCd  = profileCode is 0x0009 or 0x000A;
        bool isDvd = profileCode is 0x0011 or 0x0012 or 0x0013 or 0x0014
                                  or 0x0015 or 0x0016 or 0x0017
                                  or 0x001A or 0x001B or 0x002A or 0x002B;
        if (!isCd && !isDvd)
        {
            var name = Mmc.LookupProfile(profileCode).Name;
            throw new AudioCdBurner.BurnException(
                $"Loaded disc is {name}, not a writable CD or DVD. " +
                "Image burning currently supports CD-R/CD-RW and DVD-R/RW/+R/+RW.");
        }

        return new DataBurnPlan(
            Drive:            drive,
            ImagePath:        imagePath,
            ActualBinaryPath: actualBin,
            ImageBytes:       imageBytes,
            ImageSectors:     imageSectors,
            IsDvd:            isDvd,
            IsBinCue:         isBinCue);
    }

    public static void ExecuteBurn(DataBurnPlan plan,
                                   int? requestedSpeedX = null,
                                   Action<long, long>? onProgress = null,
                                   Action<string>? onLog = null)
    {
        var mount = plan.Drive.PrimaryMount
            ?? throw new AudioCdBurner.BurnException("Drive has no mount point.");
        char letter = mount[0];

        using var dev = SptiDevice.OpenDriveLetter(letter);

        var info = dev.ReadDiscInformation();
        if (info.Status != SptiDevice.DiscStatus.Empty)
            throw new AudioCdBurner.BurnException(
                $"Disc isn't blank (status: {info.Status}). Use a virgin disc.");

        // SET CD SPEED. CD audio 1x = 176 KB/s; DVD 1x = 1,385 KB/s. The drive
        // interprets the requested speed against current media, so we pick the
        // right multiplier per disc type.
        if (requestedSpeedX is { } x && x > 0)
        {
            int kbps = plan.IsDvd ? x * 1385 : x * 176;
            try { dev.SetCdSpeed(readSpeedKbps: 0xFFFF, writeSpeedKbps: kbps); }
            catch (Exception ex)
            {
                onLog?.Invoke($"  (SET CD SPEED to {x}x failed: {ex.Message} — proceeding at drive default)");
            }
        }

        // MODE SELECT for the right disc type.
        try
        {
            if (plan.IsDvd) dev.ConfigureForDataDvd();
            else            dev.ConfigureForDataCd();
        }
        catch (Exception ex)
        {
            throw new AudioCdBurner.BurnException(
                $"MODE SELECT 10 ({(plan.IsDvd ? "DVD" : "CD")} data) failed: {ex.Message}", ex);
        }

        // For DVD-R/+R Sequential, the drive *requires* RESERVE TRACK before
        // any WRITE 12 — without it the first WRITE comes back with sense
        // 0x5/0x2C/0x00 (COMMAND SEQUENCE ERROR). It pre-allocates the track
        // size so the drive can set up its track-boundary state machine.
        // CD-R rejects RESERVE TRACK as ILLEGAL REQUEST (it's a DVD-mode
        // command), so we only call it for DVD media.
        if (plan.IsDvd)
        {
            try { dev.ReserveTrack((int)plan.ImageSectors); }
            catch (Exception ex)
            {
                throw new AudioCdBurner.BurnException(
                    $"RESERVE TRACK ({plan.ImageSectors} sectors) for DVD failed: {ex.Message}", ex);
            }
        }

        // Write loop. 32-sector chunks = 64 KB per WRITE 12 — comfortable under
        // the SPTI 64 KB cap and the same ceiling we use for audio.
        const int chunkSectors = 32;
        var buffer = new byte[chunkSectors * DataSectorBytes];

        // For BIN/CUE input, BinCueImageStream presents the .bin's user-data
        // portion as a plain 2048-byte-per-sector stream.
        Stream stream;
        if (plan.IsBinCue)
        {
            var cue = Image.CueSheetParser.Parse(plan.ImagePath);
            var t   = cue.Tracks[0];
            stream  = new Image.BinCueImageStream(plan.ActualBinaryPath, t.Mode, t.SectorBytes);
        }
        else
        {
            stream = File.OpenRead(plan.ImagePath);
        }
        using (stream)
        {
        int currentLba = 0;
        long sectorsRemaining = plan.ImageSectors;
        long bytesWritten = 0;

        while (sectorsRemaining > 0)
        {
            int sectorsThisChunk = (int)Math.Min(chunkSectors, sectorsRemaining);
            int bytesThisChunk = sectorsThisChunk * DataSectorBytes;
            int got = ReadFully(stream, buffer, bytesThisChunk);
            // If the ISO doesn't end on a sector boundary, pad the last
            // chunk with zeros — drives only accept whole sectors.
            if (got < bytesThisChunk)
                Array.Clear(buffer, got, bytesThisChunk - got);

            int attempt = 0;
            while (true)
            {
                try
                {
                    // Explicit dataLength so DataTransferLength always matches
                    // what the CDB describes — same SPTI alignment requirement
                    // we hit on audio CD partial chunks (sense 0x29 from
                    // USB-BOT recovery if mismatched).
                    dev.Write12(currentLba, sectorsThisChunk, buffer,
                                dataLength: bytesThisChunk);
                    break;
                }
                catch (InvalidOperationException ex)
                    when (ex.Message.Contains("Win32 121") && attempt < 3)
                {
                    attempt++;
                    Thread.Sleep(250 * attempt);
                }
                catch (Exception ex)
                {
                    throw new AudioCdBurner.BurnException(
                        $"WRITE 12 failed at LBA {currentLba}: {ex.Message}", ex);
                }
            }

            currentLba       += sectorsThisChunk;
            sectorsRemaining -= sectorsThisChunk;
            bytesWritten     += Math.Min(got, plan.ImageBytes - (bytesWritten));
            onProgress?.Invoke(bytesWritten, plan.ImageBytes);
        }
        }  // using stream

        try { dev.SynchronizeCache(); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"SYNCHRONIZE CACHE failed: {ex.Message}", ex); }

        // CLOSE TRACK with explicit IMMED=1: the GE20LU10 returns instantly
        // either way, so the explicit-async pattern + observable poll is the
        // honest contract. Without an active wait here, the immediately-
        // following CLOSE SESSION sees the track as still incomplete and
        // returns sense 0x5/0x72/0x03 (SESSION FIXATION ERROR — INCOMPLETE
        // TRACK IN SESSION) — verified once on a DVD-R burn.
        try { dev.CloseTrackOrSession(function: 1, trackNumber: 1, immediate: true); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"CLOSE TRACK failed: {ex.Message}", ex); }

        // Poll READ TRACK INFO of the just-closed track until TrackSize
        // stabilizes — proof the close has actually committed (TUR is
        // unreliable on this drive for state-change waits).
        var closeDeadline = DateTime.UtcNow.AddSeconds(120);
        int lastSize = -1;
        int polls = 0;
        while (DateTime.UtcNow < closeDeadline)
        {
            polls++;
            try
            {
                var ti = dev.ReadTrackInformation(1);
                if (ti.TrackSize > 0 && ti.TrackSize == lastSize) break;
                lastSize = ti.TrackSize;
            }
            catch (SptiScsiException ex)
                when (ex.SenseKey == 0x6 || ex.SenseKey == 0x2)
            {
                // Drive busy committing the close — retry.
            }
            Thread.Sleep(500);
        }

        // CLOSE SESSION with retry on the specific session-fixation sense
        // code, which means the drive's track-close is still in flight even
        // though our poll above thinks it's done. Up to 5 attempts with
        // exponential backoff (covers ~30 seconds total).
        int sessAttempt = 0;
        while (true)
        {
            try
            {
                dev.CloseTrackOrSession(function: 2, trackNumber: 0, immediate: true);
                break;
            }
            catch (SptiScsiException ex)
                when (ex.SenseKey == 0x5 && ex.Asc == 0x72 && ex.Ascq == 0x03 && sessAttempt < 5)
            {
                sessAttempt++;
                Thread.Sleep(2000 * sessAttempt);
            }
            catch (Exception ex)
            {
                throw new AudioCdBurner.BurnException(
                    $"CLOSE SESSION failed (track-close polled stable after {polls} reads, " +
                    $"session-close attempt {sessAttempt + 1}): {ex.Message}", ex);
            }
        }
    }

    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
