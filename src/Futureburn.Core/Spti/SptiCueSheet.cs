namespace Futureburn.Core.Spti;

// Builds a SCSI MMC SEND CUE SHEET parameter list for an audio CD-DA disc.
//
// Each cue-sheet descriptor is 8 bytes:
//   byte 0: CTL (high 4 bits) | ADR (low 4 bits)
//             CTL = 0 for plain CD-DA audio (no pre-emphasis, no copy bit)
//             ADR = 1 for "current position" format (the only kind we use)
//             so byte 0 = 0x01 for CD-DA tracks; or 0x10 if your interpretation
//             swaps the nibbles. We use the MMC-6 layout: byte 0 = 0x01 for audio.
//   byte 1: TNO — track number (00 = lead-in, 01-99 = tracks, AA = lead-out)
//   byte 2: INDEX — 00 = pre-gap, 01 = track body, A0/A1/A2 = special pointers
//   byte 3: DATA FORM — 0x00 for CD-DA mode 0 (raw 2352-byte audio)
//   byte 4: SCMS / reserved — 0
//   byte 5: M (MSF minute, BCD)
//   byte 6: S (MSF second, BCD)
//   byte 7: F (MSF frame, BCD; 75 frames = 1 second)
//
// The first three descriptors are special "pointer" entries:
//   A0: first track number, session format
//   A1: last track number
//   A2: lead-out start position
//
// Then for each audio track:
//   Track N, INDEX 0: pre-gap start (LBA position; 0-frame for gapless)
//   Track N, INDEX 1: track body start (LBA position)
//
// MSF is in absolute CD time, where LBA 0 of the user-data area corresponds to
// MSF 00:02:00 (a 2-second offset for the lead-in). LBA -150 is MSF 00:00:00.
//
// **STATUS:** This builder hasn't been validated against a real disc burn yet.
// The MMC SEND CUE SHEET format is precise about bit layout, BCD encoding, and
// pointer-entry structure; getting any byte wrong typically yields a bricked
// CD-R. The first user to burn with `--gapless` is doing the validation. If
// it fails, the cue sheet bytes are emitted in the burn log so we can debug.

public static class SptiCueSheet
{
    public sealed record Track(long LengthSectors);

    /// <summary>
    /// Build the cue sheet for a gapless audio CD with the given track lengths.
    /// </summary>
    public static byte[] BuildAudioCd(IReadOnlyList<Track> tracks, bool gapless = true)
    {
        if (tracks.Count == 0) throw new ArgumentException("Need at least one track", nameof(tracks));
        if (tracks.Count > 99)  throw new ArgumentException("CD-DA limit is 99 tracks",  nameof(tracks));

        // Compute each track's start LBA (track 1 starts at 0).
        var startsLba = new long[tracks.Count];
        long cursor = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            startsLba[i] = cursor;
            cursor += tracks[i].LengthSectors;
            if (!gapless && i < tracks.Count - 1)
            {
                // Standard Red Book inter-track pre-gap = 150 sectors (2 sec).
                cursor += 150;
            }
        }
        long leadOutLba = cursor;

        // Now build descriptors:
        //   3 pointer entries (A0/A1/A2)
        //   2 entries per track (INDEX 0 + INDEX 1) for tracks where pre-gap exists,
        //   or 1 entry per track for fully gapless tracks 2..N. Track 1 always has
        //   an INDEX 0 entry for the lead-in transition.
        var descriptors = new List<byte[]>(3 + tracks.Count * 2);

        // A0: first track number = 1, session format = 0 (CD-DA / CD-ROM).
        // A0 special encoding: byte 5 = first track (binary, NOT BCD), byte 6 = session format.
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0x00, index: 0xA0, dataForm: 0x00,
                                        b5: 0x01, b6: 0x00, b7: 0x00));

        // A1: last track number.
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0x00, index: 0xA1, dataForm: 0x00,
                                        b5: (byte)tracks.Count, b6: 0x00, b7: 0x00));

        // A2: lead-out start position.
        var (loM, loS, loF) = LbaToMsfBcd(leadOutLba);
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0x00, index: 0xA2, dataForm: 0x00,
                                        b5: loM, b6: loS, b7: loF));

        // Per-track entries.
        for (int i = 0; i < tracks.Count; i++)
        {
            byte tno = (byte)((i + 1) % 100);  // BCD-style would be different; cue sheet uses binary 01-99
            var (m, s, f) = LbaToMsfBcd(startsLba[i]);

            // INDEX 0 (pre-gap entry). For track 1 it's at MSF 00:00:00 (lead-in handoff).
            // For tracks 2..N in gapless mode, Index 0 == Index 1 (zero-length pre-gap).
            // For non-gapless mode, Index 0 is 150 sectors before Index 1.
            long index0Lba = i == 0
                ? -150  // before any user data
                : (gapless ? startsLba[i] : startsLba[i] - 150);
            var (m0, s0, f0) = LbaToMsfBcd(index0Lba);
            descriptors.Add(MakeDescriptor(ctl: 0x01, tno: tno, index: 0x00, dataForm: 0x00,
                                            b5: m0, b6: s0, b7: f0));

            // INDEX 1 (track body start).
            descriptors.Add(MakeDescriptor(ctl: 0x01, tno: tno, index: 0x01, dataForm: 0x00,
                                            b5: m,  b6: s,  b7: f));
        }

        // Lead-out marker.
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0xAA, index: 0x01, dataForm: 0x00,
                                        b5: loM, b6: loS, b7: loF));

        // Concatenate.
        var result = new byte[descriptors.Count * 8];
        for (int i = 0; i < descriptors.Count; i++)
            Array.Copy(descriptors[i], 0, result, i * 8, 8);
        return result;
    }

    private static byte[] MakeDescriptor(byte ctl, byte tno, byte index, byte dataForm,
                                         byte b5, byte b6, byte b7)
    {
        // ctl is the byte-0 value (CTL << 4 | ADR), but we accept it pre-packed.
        return new byte[] { ctl, tno, index, dataForm, 0x00, b5, b6, b7 };
    }

    /// <summary>
    /// Convert LBA (offset within the user-data area, with LBA 0 = MSF 00:02:00)
    /// to MSF (Min, Sec, Frame) in BCD encoding. LBA -150 = MSF 00:00:00.
    /// </summary>
    public static (byte M, byte S, byte F) LbaToMsfBcd(long lba)
    {
        long abs = lba + 150;
        if (abs < 0) abs = 0;
        long minutes = abs / (60 * 75);
        long rem     = abs - minutes * 60 * 75;
        long seconds = rem / 75;
        long frames  = rem - seconds * 75;
        return (ToBcd((int)minutes), ToBcd((int)seconds), ToBcd((int)frames));
    }

    private static byte ToBcd(int n)
    {
        // BCD: tens digit in upper nibble, units digit in lower nibble.
        if (n < 0 || n > 99) throw new ArgumentOutOfRangeException(nameof(n));
        return (byte)(((n / 10) << 4) | (n % 10));
    }

    /// <summary>Pretty-print the cue sheet bytes for debugging / burn logs.</summary>
    public static string Dump(byte[] cueSheet)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Cue sheet (" + cueSheet.Length + " bytes, " + (cueSheet.Length / 8) + " entries):");
        sb.AppendLine("  CTL TNO IDX DAT  -- M  S  F   (raw bytes)");
        for (int i = 0; i < cueSheet.Length; i += 8)
        {
            sb.AppendFormat("  {0:X2}  {1:X2}  {2:X2}  {3:X2}  -- {4:X2} {5:X2} {6:X2}   ({7})\n",
                cueSheet[i + 0], cueSheet[i + 1], cueSheet[i + 2], cueSheet[i + 3],
                cueSheet[i + 5], cueSheet[i + 6], cueSheet[i + 7],
                BitConverter.ToString(cueSheet, i, 8));
        }
        return sb.ToString();
    }
}
