using System.Text;

namespace Futureburn.Core.Authoring;

// Builds the binary VCD/ENTRIES.VCD file (always 2048 bytes).
//
// Each entry points at where one of the disc's MMC tracks begins, in MSF
// (Min/Sec/Frame, BCD). For a normal VCD:
//   Track 1 = the data track that holds VCD/, MPEGAV/, etc. (the file system)
//   Tracks 2+ = the AVSEQ##.DAT video tracks (each one its own MMC track)
//
// Layout:
//   0-7   : "ENTRYVCD" 8-byte ASCII signature
//   8     : Version (0x02 for VCD 2.0)
//   9     : Reserved
//   10-11 : Number of entries (BE16, max 500)
//   12+   : Per-entry blocks of 10 bytes:
//             [0]   : MMC track number, BCD (00..99)
//             [1-3] : MSF position, BCD (M, S, F where F = frames 0..74)
//             [4-9] : 6 reserved bytes (zero)
//   ...   : pad to 2048
//
// Important: this produces a file that's binary-correct per the VCD 2.0
// spec, BUT the LBA → MSF positions are only meaningful once you know the
// final disc layout. Our current burn pipeline writes single-track data
// CDs, so we burn the VCD folder as one big track 1. Strict-spec VCD
// players expect multi-track CDs (file system on track 1, video on
// tracks 2+). The video may still play in software players (VLC etc.)
// even on our single-track burn — that's the experimental caveat.

public static class VcdEntriesBuilder
{
    public sealed record TrackEntry(int MmcTrackNumber, long StartLba);

    public static byte[] Build(IReadOnlyList<TrackEntry> tracks)
    {
        var p = new byte[2048];
        Encoding.ASCII.GetBytes("ENTRYVCD").CopyTo(p, 0);
        p[8] = 0x02;

        int n = Math.Min(tracks.Count, 500);
        p[10] = (byte)((n >> 8) & 0xFF);
        p[11] = (byte)( n       & 0xFF);

        int offset = 12;
        for (int i = 0; i < n; i++)
        {
            var t = tracks[i];
            p[offset]     = ToBcd(Math.Clamp(t.MmcTrackNumber, 0, 99));
            var (m, s, f) = LbaToMsfBcd(t.StartLba);
            p[offset + 1] = m;
            p[offset + 2] = s;
            p[offset + 3] = f;
            // 6 reserved bytes (already zero from new byte[2048])
            offset += 10;
        }

        return p;
    }

    public static byte ToBcd(int n)
    {
        if (n < 0 || n > 99) throw new ArgumentOutOfRangeException(nameof(n));
        return (byte)(((n / 10) << 4) | (n % 10));
    }

    public static (byte M, byte S, byte F) LbaToMsfBcd(long lba)
    {
        long abs = lba + 150;
        if (abs < 0) abs = 0;
        long min = abs / (60 * 75);
        long rem = abs - min * 60 * 75;
        long sec = rem / 75;
        long frm = rem - sec * 75;
        return (ToBcd((int)min), ToBcd((int)sec), ToBcd((int)frm));
    }
}
