using System.Runtime.Versioning;
using Futureburn.Core.Imapi;
using Futureburn.Core.Spti;

namespace Futureburn.Core.Image;

// Rip a physical data disc (CD-ROM / DVD-ROM / BD-ROM / any finalized data
// disc) to an ISO image via SPTI READ (10). This is Daemon Tools' "grab image
// from disc" — Tier B user-mode work, no driver.
//
// Scope: 2048-byte data discs. Audio CDs (2352-byte CD-DA) rip to WAV, which is
// a different pipeline (the read command and the file format both differ), so we
// reject them here with a clear message rather than producing a broken ISO.

[SupportedOSPlatform("windows")]
public static class DiscRipper
{
    public const int SectorBytes = 2048;

    public sealed record RipPlan(OpticalDrive Drive, long TotalSectors, int BlockSize)
    {
        public long TotalBytes => TotalSectors * BlockSize;
    }

    public sealed record RipResult(long BytesWritten, long BadSectors);

    public static RipPlan Plan(OpticalDrive drive)
    {
        var mount = drive.PrimaryMount
            ?? throw new InvalidOperationException("Drive has no mount point.");
        using var dev = SptiDevice.OpenDriveLetter(mount[0]);
        try { dev.WaitUntilReady(timeoutSec: 20); } catch { }

        var (lastLba, blockSize) = dev.ReadCapacity10();
        if (lastLba <= 0)
            throw new InvalidOperationException(
                "Drive reports no readable data (is a finalized data disc loaded?).");
        if (blockSize != SectorBytes)
            throw new InvalidOperationException(
                $"Disc reports {blockSize}-byte sectors — rip supports 2048-byte data discs (→ ISO). " +
                "Audio CDs rip to WAV, which is a separate path.");
        return new RipPlan(drive, lastLba + 1, blockSize);
    }

    /// <summary>
    /// Read the whole disc to <paramref name="outputIso"/>. Persistently
    /// unreadable sectors (a scratched or intentionally short-padded disc) are
    /// zero-filled after retries and counted — the rip completes rather than
    /// aborting, and the bad-sector count is returned so the caller can warn.
    /// </summary>
    public static RipResult Rip(RipPlan plan, string outputIso,
                                Action<long, long>? onProgress = null,
                                Action<string>? onLog = null)
    {
        char letter = plan.Drive.PrimaryMount![0];
        using var dev = SptiDevice.OpenDriveLetter(letter);
        try { dev.WaitUntilReady(timeoutSec: 20); } catch { }

        const int chunkSectors = 32;                 // 64 KB per READ (10)
        var buffer = new byte[chunkSectors * SectorBytes];

        using var outStream = File.Create(outputIso);
        long remaining = plan.TotalSectors, badSectors = 0, bytesWritten = 0;
        int lba = 0;

        while (remaining > 0)
        {
            int n = (int)Math.Min(chunkSectors, remaining);
            int bytes = n * SectorBytes;
            bool ok = false;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                try { dev.Read10(lba, n, buffer, dataLength: bytes); ok = true; }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        // Give up on this chunk — zero-fill so byte offsets stay
                        // aligned, count it, and keep going.
                        Array.Clear(buffer, 0, bytes);
                        badSectors += n;
                        onLog?.Invoke($"  unreadable at LBA {lba} (+{n} sectors) — zero-filled: {ex.Message}");
                    }
                    else Thread.Sleep(150 * (attempt + 1));
                }
            }
            outStream.Write(buffer, 0, bytes);
            lba += n; remaining -= n; bytesWritten += bytes;
            onProgress?.Invoke(bytesWritten, plan.TotalBytes);
        }
        return new RipResult(bytesWritten, badSectors);
    }
}
