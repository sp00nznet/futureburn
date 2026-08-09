using System.Runtime.Versioning;
using Futureburn.Core.Imapi;

namespace Futureburn.Core.Spti;

// Erase a rewritable optical disc — Daemon Tools' "erase discs", Tier B.
//   CD-RW, DVD-RW               → BLANK (0xA1): minimal (fast) or full.
//   DVD+RW, DVD-RAM, BD-RE      → FORMAT UNIT (0x04): these are random-overwrite
//                                 media and reject BLANK; a quick format is the
//                                 equivalent "make it empty again".
// Write-once media (CD-R, DVD-R, BD-R) cannot be erased — rejected up front.

[SupportedOSPlatform("windows")]
public static class DiscEraser
{
    public sealed record EraseResult(string MediaName, string Method);

    public static EraseResult Erase(OpticalDrive drive, bool full = false,
                                    Action<string>? onLog = null)
    {
        var mount = drive.PrimaryMount
            ?? throw new InvalidOperationException("Drive has no mount point.");
        int profile = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
        string name = Mmc.LookupProfile(profile).Name;

        using var dev = SptiDevice.OpenDriveLetter(mount[0]);
        try { dev.WaitUntilReady(timeoutSec: 20); } catch { }

        string method;
        switch (profile)
        {
            case 0x000A:                       // CD-RW
            case 0x0013: case 0x0014:          // DVD-RW (restricted overwrite / sequential)
                onLog?.Invoke($"{name}: BLANK ({(full ? "full" : "minimal/fast")}) ...");
                dev.Blank(minimal: !full, immediate: true);
                method = full ? "BLANK (full)" : "BLANK (minimal)";
                break;

            case 0x0012:                       // DVD-RAM
            case 0x001A:                       // DVD+RW
            case 0x0043:                       // BD-RE
                onLog?.Invoke($"{name}: FORMAT UNIT (quick) ...");
                dev.FormatUnit(immediate: 1);
                method = "FORMAT UNIT";
                break;

            case 0x0009: case 0x0011: case 0x0015: case 0x0016:
            case 0x001B: case 0x002B: case 0x0041: case 0x0042:
                throw new InvalidOperationException(
                    $"{name} is write-once — it can't be erased. Erase needs a rewritable disc " +
                    "(CD-RW, DVD-RW/+RW, DVD-RAM, or BD-RE).");
            default:
                throw new InvalidOperationException(
                    $"No disc, or a non-erasable disc ({name}) is loaded.");
        }

        // BLANK/FORMAT run with IMMED=1 — the drive erases asynchronously. Poll
        // until it reports ready and the disc reads back as Empty (a full CD-RW
        // blank can take many minutes; a minimal blank or +RW format is quick).
        onLog?.Invoke("  erasing (this can take a while for a full blank) ...");
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                dev.WaitUntilReady(timeoutSec: 30);
                var di = dev.ReadDiscInformation();
                if (di.Status == SptiDevice.DiscStatus.Empty) break;
            }
            catch (SptiScsiException ex) when (ex.SenseKey is 0x2 or 0x6)
            {
                // NOT READY / UNIT ATTENTION while erasing — keep waiting.
            }
            Thread.Sleep(2000);
        }
        onLog?.Invoke("  erase complete.");
        return new EraseResult(name, method);
    }
}
