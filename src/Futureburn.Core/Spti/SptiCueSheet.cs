namespace Futureburn.Core.Spti;

// Builds a SCSI MMC SEND CUE SHEET (opcode 0x5D) parameter list for an audio
// CD-DA disc burned Disc-At-Once / Session-At-Once.
//
// Each cue-sheet descriptor is 8 bytes (MMC-5 §6.26.3.4, Table 489):
//   byte 0: CTL (high nibble) | ADR (low nibble)
//             CTL = 0 for plain CD-DA audio; ADR = 1 ("time point").
//             So byte 0 = 0x01 for every audio track/lead-in/lead-out entry.
//   byte 1: TNO — track number. 0 = lead-in, 1..99 = tracks, 0xAA = lead-out.
//   byte 2: INDEX — 0 = pre-gap, 1 = track body / lead-out marker.
//   byte 3: DATA FORM — 0x00 for CD-DA audio payload; 0x01 ("audio pause")
//             for the lead-in and lead-out entries. OR'd with 0x40 on the
//             lead-in entry when CD-Text is written into the lead-in (→ 0x41).
//   byte 4: SCMS / reserved — 0.
//   byte 5: M (MSF minute)   ─┐
//   byte 6: S (MSF second)    ├─ PLAIN BINARY, not BCD (see below).
//   byte 7: F (MSF frame)    ─┘  75 frames = 1 second.
//
// The descriptor stream is:
//   1 lead-in entry  (TNO 0, INDEX 0, MSF 00:00:00 = LBA -150)
//   2 entries per track (INDEX 0 pre-gap + INDEX 1 body)
//   1 lead-out entry (TNO 0xAA, INDEX 1, at the lead-out address)
// — no A0/A1/A2 pointer descriptors: the drive derives first/last track and
// the lead-out address from the stream itself (this is what cdrecord and
// libburn do; A0/A1/A2 descriptors are not part of a SEND CUE SHEET).
//
// **BINARY, not BCD.** The SEND CUE SHEET parameter list is plain binary
// throughout — track numbers and every MSF field. The drive's firmware
// converts to BCD itself when it writes the subcode Q-channel. An earlier
// version of this file BCD-encoded everything ("to line up with the
// Q-channel"); that was wrong and made the GE20LU10 reject SEND CUE SHEET
// with sense 0x5/0x26/0x00 (INVALID FIELD IN PARAMETER LIST). cdrecord
// (drv_mmc.c gen_cue_mmc/fillcue/lba_to_msf) and libburn (write.c add_cue)
// both emit binary. Confirmed against MMC-5.
//
// MSF is absolute CD time: LBA 0 of the user-data area = MSF 00:02:00 (a
// 2-second lead-in offset); LBA -150 = MSF 00:00:00.

public static class SptiCueSheet
{
    public sealed record Track(long LengthSectors);

    /// <summary>
    /// Build the cue sheet for an audio CD with the given track lengths.
    /// When <paramref name="cdText"/> is true the lead-in entry's DATA FORM
    /// gets the 0x40 "CD-Text in lead-in" bit.
    /// </summary>
    public static byte[] BuildAudioCd(IReadOnlyList<Track> tracks, bool gapless = true,
                                      bool cdText = false)
    {
        if (tracks.Count == 0) throw new ArgumentException("Need at least one track", nameof(tracks));
        if (tracks.Count > 99)  throw new ArgumentException("CD-DA limit is 99 tracks",  nameof(tracks));

        // Each track's start LBA (track 1 starts at 0). Non-gapless inserts a
        // standard 150-sector (2 s) pre-gap before each track after the first.
        var startsLba = new long[tracks.Count];
        long cursor = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            startsLba[i] = cursor;
            cursor += tracks[i].LengthSectors;
            if (!gapless && i < tracks.Count - 1)
                cursor += 150;
        }
        long leadOutLba = cursor;

        // 1 lead-in + 2 per track + 1 lead-out.
        var descriptors = new List<byte[]>(2 + tracks.Count * 2);

        // Lead-in: TNO 0, INDEX 0, MSF 00:00:00 (LBA -150). DATA FORM 0x01 =
        // audio pause; 0x41 when CD-Text rides in the lead-in.
        byte leadInForm = cdText ? (byte)0x41 : (byte)0x01;
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0x00, index: 0x00, dataForm: leadInForm,
                                        m: 0, s: 0, f: 0));

        // Per-track INDEX 0 (pre-gap) + INDEX 1 (body). Track numbers binary.
        for (int i = 0; i < tracks.Count; i++)
        {
            byte tno = (byte)(i + 1);
            long index1Lba = startsLba[i];
            // Gapless: INDEX 0 == INDEX 1. Non-gapless: INDEX 0 is 150 sectors
            // earlier (for track 1 that lands at LBA -150 = 00:00:00).
            long index0Lba = gapless ? index1Lba : index1Lba - 150;

            var (m0, s0, f0) = LbaToMsf(index0Lba);
            descriptors.Add(MakeDescriptor(ctl: 0x01, tno: tno, index: 0x00, dataForm: 0x00,
                                            m: m0, s: s0, f: f0));

            var (m1, s1, f1) = LbaToMsf(index1Lba);
            descriptors.Add(MakeDescriptor(ctl: 0x01, tno: tno, index: 0x01, dataForm: 0x00,
                                            m: m1, s: s1, f: f1));
        }

        // Lead-out: TNO 0xAA, INDEX 1, DATA FORM 0x01 (audio pause).
        var (loM, loS, loF) = LbaToMsf(leadOutLba);
        descriptors.Add(MakeDescriptor(ctl: 0x01, tno: 0xAA, index: 0x01, dataForm: 0x01,
                                        m: loM, s: loS, f: loF));

        var result = new byte[descriptors.Count * 8];
        for (int i = 0; i < descriptors.Count; i++)
            Array.Copy(descriptors[i], 0, result, i * 8, 8);
        return result;
    }

    private static byte[] MakeDescriptor(byte ctl, byte tno, byte index, byte dataForm,
                                         byte m, byte s, byte f)
        => new byte[] { ctl, tno, index, dataForm, 0x00, m, s, f };

    /// <summary>
    /// Convert an LBA (LBA 0 = MSF 00:02:00, LBA -150 = MSF 00:00:00) to its
    /// absolute MSF as plain binary bytes.
    /// </summary>
    public static (byte M, byte S, byte F) LbaToMsf(long lba)
    {
        long abs = lba + 150;
        if (abs < 0) abs = 0;
        long minutes = abs / (60 * 75);
        long rem     = abs - minutes * 60 * 75;
        long seconds = rem / 75;
        long frames  = rem - seconds * 75;
        return ((byte)minutes, (byte)seconds, (byte)frames);
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
