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
        long ImageBytes,
        long ImageSectors,
        bool IsDvd);

    public static DataBurnPlan Plan(OpticalDrive drive, string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new AudioCdBurner.BurnException($"Image file not found: {imagePath}");

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

        var fi = new FileInfo(imagePath);
        long imageSectors = (fi.Length + DataSectorBytes - 1) / DataSectorBytes;

        return new DataBurnPlan(drive, imagePath, fi.Length, imageSectors, isDvd);
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

        // Write loop. 32-sector chunks = 64 KB per WRITE 12 — comfortable under
        // the SPTI 64 KB cap and the same ceiling we use for audio.
        const int chunkSectors = 32;
        var buffer = new byte[chunkSectors * DataSectorBytes];

        using var stream = File.OpenRead(plan.ImagePath);
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
                    dev.Write12(currentLba, sectorsThisChunk, buffer);
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

        try { dev.SynchronizeCache(); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"SYNCHRONIZE CACHE failed: {ex.Message}", ex); }

        try { dev.CloseTrackOrSession(function: 1, trackNumber: 1); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"CLOSE TRACK failed: {ex.Message}", ex); }

        try { dev.CloseTrackOrSession(function: 2, trackNumber: 0); }
        catch (Exception ex)
        { throw new AudioCdBurner.BurnException($"CLOSE SESSION failed: {ex.Message}", ex); }
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
