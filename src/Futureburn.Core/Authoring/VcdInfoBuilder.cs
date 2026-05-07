using System.Text;

namespace Futureburn.Core.Authoring;

// Builds the binary VCD/INFO.VCD file (always 2048 bytes) per the VCD 2.0
// White Book spec.
//
// Layout (best-effort, marked experimental until tested against a real
// standalone VCD player):
//   0-7   : "VIDEO_CD" 8-byte ASCII signature
//   8     : System Profile Tag (1 = VCD 1.0, 2 = VCD 1.1, 3 = VCD 2.0)
//   9     : Album Version (BCD; 0x02 for v2.0)
//   10-25 : Album Identification (16 bytes ASCII, space-padded)
//   26-27 : Volume Count (BE16, usually 1)
//   28-29 : Volume Number (BE16, usually 1)
//   30    : PAL flag (high bit; 0x80 = PAL, 0x00 = NTSC per most refs)
//   31    : Pub flag
//   32+   : PSD info, segment info, restriction flags — all zeros for
//           a no-menu, no-segment, no-restriction disc.
//   ...   : pad to 2048
//
// We don't author menus or segments. The resulting disc plays its tracks
// in order. That's the right shape for "drop a movie, get a playable disc."

public static class VcdInfoBuilder
{
    public static byte[] Build(string albumLabel, bool palMode,
                                int volumeCount = 1, int volumeNumber = 1,
                                int systemProfile = 2)
    {
        var p = new byte[2048];

        Encoding.ASCII.GetBytes("VIDEO_CD").CopyTo(p, 0);

        p[8] = (byte)systemProfile;     // 2 = VCD 1.1 (broadly compatible)
        p[9] = 0x02;                     // album version (BCD)

        var album = (albumLabel ?? "FUTUREBURN").ToUpperInvariant();
        if (album.Length > 16) album = album.Substring(0, 16);
        else                   album = album.PadRight(16);
        Encoding.ASCII.GetBytes(album).CopyTo(p, 10);

        p[26] = (byte)((volumeCount  >> 8) & 0xFF);
        p[27] = (byte)( volumeCount        & 0xFF);
        p[28] = (byte)((volumeNumber >> 8) & 0xFF);
        p[29] = (byte)( volumeNumber       & 0xFF);

        // Bit 7 = PAL flag (some refs say bit 4; varies). Best-effort.
        p[30] = palMode ? (byte)0x80 : (byte)0x00;

        // Everything past byte 31 is zero in a minimal no-menu / no-segment VCD.
        return p;
    }
}
