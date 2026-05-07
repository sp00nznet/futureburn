using System.Text;

namespace Futureburn.Core.Authoring;

// Minimal builders for DVD-Video VIDEO_TS.IFO and VTS_##_0.IFO files.
//
// HONEST SCOPE: a real, spec-compliant IFO is hundreds of bytes of nested
// table structures (TT_SRPT, VTS_ATRT, VMGM/VTSM_PGCI_UT, PTL_MAIT, plus
// the VTS_C_ADT cell address table and the VTS_VOBU_ADMAP, the latter of
// which requires scanning the VOB for NAV packets to find every VOBU
// boundary). That level of authoring is its own multi-session subsystem.
//
// What we write here is the BARE MINIMUM: the 12-byte ASCII signature
// every reader looks for, the spec version byte, the title-set count,
// and zeros for everything else. The result is enough that:
//   - The disc has a recognizable DVD-Video file layout
//   - VLC and other software players that can "Open VOB / Open VIDEO_TS
//     folder" play the content directly without IFO parsing
// It's NOT enough for strict standalone DVD players, which read the
// IFO structures to navigate. For production-quality DVDs, use a real
// authoring tool (DVDStyler, DVDFlick, dvdauthor) that produces full
// IFOs, then `burn-folder` the result.

public static class DvdIfoBuilder
{
    /// <summary>
    /// Build a minimal VIDEO_TS.IFO (2048 bytes). The file starts with the
    /// "DVDVIDEO-VMG" signature, declares spec version 1.0 and one title set,
    /// and is otherwise zeros. Software players that don't strictly parse
    /// the IFO will accept it; standalone players probably won't.
    /// </summary>
    public static byte[] BuildVmgIfo(int numTitleSets = 1, string providerId = "FUTUREBURN")
    {
        var p = new byte[2048];
        // Bytes 0-11: signature
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(p, 0);
        // Byte 33: specification version. 0x10 = DVD-Video v1.0
        // (this is the second byte of a 2-byte field at 32-33; first byte zero)
        p[33] = 0x10;
        // Bytes 38-39: number of volumes (BE16) = 1
        p[39] = 0x01;
        // Bytes 40-41: this volume number (BE16) = 1
        p[41] = 0x01;
        // Byte 42: disc side = 1 (Side A)
        p[42] = 0x01;
        // Bytes 62-63: number of title sets (BE16)
        p[62] = (byte)((numTitleSets >> 8) & 0xFF);
        p[63] = (byte)( numTitleSets       & 0xFF);
        // Bytes 64-95: provider identifier (ASCII, up to 32 chars)
        var prov = (providerId ?? "").ToUpperInvariant();
        if (prov.Length > 32) prov = prov.Substring(0, 32);
        Encoding.ASCII.GetBytes(prov.PadRight(32)).CopyTo(p, 64);
        // Everything else is zero — pointers to tables that don't exist in
        // our minimal disc (no menus, no parental mgmt, no text data).
        return p;
    }

    /// <summary>
    /// Build a minimal VTS_##_0.IFO (2048 bytes) — title set info file.
    /// Same minimal philosophy as BuildVmgIfo: signature + spec version,
    /// rest zero. Software players accept it; standalone players probably won't.
    /// </summary>
    public static byte[] BuildVtsIfo()
    {
        var p = new byte[2048];
        // Bytes 0-11: signature
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(p, 0);
        // Byte 33: spec version (0x10 = DVD-Video v1.0)
        p[33] = 0x10;
        // Everything else zero.
        return p;
    }
}
