using System.Runtime.Versioning;

namespace Futureburn.Core.Spti;

// Stub for the SPTI-based audio CD burn engine. The full implementation will:
//
//   1. Open the drive with SptiDevice.OpenDriveLetter (needs admin/elevated)
//   2. Read disc state via READ DISC INFORMATION (0x51)
//   3. Read available capacity via READ TRACK INFORMATION (0x52) on track 0
//   4. SET CD SPEED (0xBB) to negotiate write speed
//   5. MODE SELECT 10 (0x55) with Write Parameters Mode Page 0x05 to set
//      track-at-once + audio CD-DA write type
//   6. RESERVE TRACK (0x53) for each audio track to claim sector ranges
//   7. For each track:
//        a. WRITE 12 (0xAA) loop, sending raw 2352-byte CD-DA frames
//        b. SYNCHRONIZE CACHE (0x35) to flush
//        c. CLOSE TRACK (0x5B) to close the track session
//   8. CLOSE SESSION (0x5B with different params) to finalize
//
// All of this without going through IMAPI at all. Same approach as ImgBurn.
// Significantly more code than the IMAPI engines, but immune to IMAPI's
// drive-specific compatibility quirks.

[SupportedOSPlatform("windows")]
public static class SptiBurnEngine
{
    public static void ExecuteBurn(/* TBD */)
    {
        throw new NotImplementedException(
            "SPTI burn engine is scaffolded but not yet implemented. " +
            "For now, use --engine v2 (default) or --engine v1 (legacy fallback).");
    }
}
