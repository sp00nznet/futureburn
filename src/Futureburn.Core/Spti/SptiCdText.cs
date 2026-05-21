using System.Text;

namespace Futureburn.Core.Spti;

// Builds the CD-Text pack stream that puts artist / album / track titles into a
// CD's lead-in, so car stereos and CD players show "Whatever You Want" instead
// of "Track 16".
//
// CD-Text lives in the R-W subchannel of the disc lead-in. The host hands the
// recorder a stream of fixed-size 18-byte "packs"; the drive interleaves them
// into the lead-in subcode. Each pack is:
//
//   byte 0     Pack type    (0x80 title, 0x81 performer, 0x8F size info, ...)
//   byte 1     Track number (0 = disc/album level; 1..99 = a track)
//   byte 2     Sequence number — a plain 0-based counter across the block
//   byte 3     Block & character-position byte:
//                bit 7    double-byte charset flag (we always emit 0 = single)
//                bits 6-4 block number (one block per language; we use block 0)
//                bits 3-0 character position — how many bytes of THIS pack's
//                         first track's text already appeared in earlier packs
//                         (capped at 15). Lets a reader resync mid-string.
//   bytes 4-15 12 bytes of text payload
//   bytes 16-17 CRC-16 over bytes 0-15, big-endian
//
// Text for one pack type is a single continuous stream: the disc-level string
// (track 0), then each track's string, every string NUL-terminated. That stream
// is chopped into 12-byte payloads — a string longer than 12 bytes simply spans
// consecutive packs of the same type, and one pack can hold the tail of one
// string and the head of the next.
//
// The 0x8F "size info" packs are mandatory: three packs whose 36 bytes of
// payload tell the reader the charset, track range, and how many packs of each
// type exist. Without correct 0x8F packs many players show nothing at all.
//
// References: MMC-3 Annex J, Sony CD-Text spec, libburn doc/cdtext.txt,
// cdrdao dao/CdTextEncoder.cc (the CRC algorithm is verified against the latter).

public static class SptiCdText
{
    public const byte PackTitle     = 0x80;   // disc title / track titles
    public const byte PackPerformer = 0x81;   // disc artist / track artists
    public const byte PackSizeInfo  = 0x8F;   // block size information (mandatory)

    /// <summary>One 18-byte pack.</summary>
    public const int PackSize = 18;

    private const byte CharCodeIso8859_1 = 0x00;  // Latin-1; broadest player support
    private const byte LanguageEnglish   = 0x09;  // EBU Tech 3264 language code
    private const int  MaxStringLength   = 160;   // practical per-field cap
    private const int  MaxPacks          = 255;   // single-byte sequence number

    /// <summary>Per-track CD-Text metadata. Null fields are simply omitted.</summary>
    public sealed record Track(string? Title, string? Performer);

    /// <summary>Whole-disc CD-Text metadata.</summary>
    public sealed record Disc(
        string? AlbumTitle,
        string? AlbumPerformer,
        IReadOnlyList<Track> Tracks)
    {
        /// <summary>True when there's at least one string worth encoding.</summary>
        public bool HasAnything =>
            !string.IsNullOrWhiteSpace(AlbumTitle) ||
            !string.IsNullOrWhiteSpace(AlbumPerformer) ||
            Tracks.Any(t => !string.IsNullOrWhiteSpace(t.Title) ||
                            !string.IsNullOrWhiteSpace(t.Performer));
    }

    /// <summary>
    /// Build the raw 18-byte CD-Text pack stream for the given disc metadata.
    /// The result length is always a multiple of 18.
    /// </summary>
    public static byte[] Build(Disc disc)
    {
        if (disc.Tracks.Count == 0)
            throw new ArgumentException("Need at least one track", nameof(disc));
        if (disc.Tracks.Count > 99)
            throw new ArgumentException("CD-DA limit is 99 tracks", nameof(disc));
        if (!disc.HasAnything)
            throw new ArgumentException("No CD-Text metadata to encode", nameof(disc));

        const byte block = 0;   // single language block
        var packs = new List<byte[]>();

        // --- TITLE packs (0x80): album title at track 0, track titles at 1..N.
        bool hasTitle = !string.IsNullOrWhiteSpace(disc.AlbumTitle)
                        || disc.Tracks.Any(t => !string.IsNullOrWhiteSpace(t.Title));
        if (hasTitle)
        {
            var entries = new List<string> { disc.AlbumTitle ?? "" };
            entries.AddRange(disc.Tracks.Select(t => t.Title ?? ""));
            AppendTextPacks(packs, PackTitle, entries, block);
        }

        // --- PERFORMER packs (0x81): album artist at track 0, per-track artist
        // at 1..N. Tracks with no explicit performer inherit the album artist —
        // that's what a single-artist album wants, and players expect every
        // track to carry a performer entry.
        bool hasPerformer = !string.IsNullOrWhiteSpace(disc.AlbumPerformer)
                            || disc.Tracks.Any(t => !string.IsNullOrWhiteSpace(t.Performer));
        if (hasPerformer)
        {
            var entries = new List<string> { disc.AlbumPerformer ?? "" };
            entries.AddRange(disc.Tracks.Select(t =>
                !string.IsNullOrWhiteSpace(t.Performer) ? t.Performer!
                                                        : disc.AlbumPerformer ?? ""));
            AppendTextPacks(packs, PackPerformer, entries, block);
        }

        int textPacks = packs.Count;

        // --- SIZE INFO packs (0x8F): always exactly 3, appended last.
        // Their pack-type counts must include themselves, so count now.
        var counts = new Dictionary<byte, int>();
        foreach (var p in packs)
            counts[p[0]] = counts.GetValueOrDefault(p[0]) + 1;
        counts[PackSizeInfo] = 3;

        int totalPacks = textPacks + 3;
        if (totalPacks > MaxPacks)
            throw new ArgumentException(
                $"Too much CD-Text metadata: {totalPacks} packs exceeds the {MaxPacks}-pack limit. " +
                "Shorten some titles or artist names.", nameof(disc));

        byte[] sizeRecord = BuildSizeInfoRecord(
            charCode: CharCodeIso8859_1,
            firstTrack: 1,
            lastTrack: disc.Tracks.Count,
            packCounts: counts,
            lastSequenceBlock0: totalPacks - 1);

        for (int i = 0; i < 3; i++)
        {
            var pack = new byte[PackSize];
            pack[0] = PackSizeInfo;
            pack[1] = (byte)i;                       // track number 0, 1, 2
            pack[2] = 0;                             // sequence — filled below
            pack[3] = (byte)((block & 0x07) << 4);   // block 0, char position 0
            Array.Copy(sizeRecord, i * 12, pack, 4, 12);
            packs.Add(pack);
        }

        // --- Stamp sequence numbers (0..N in pack order) and CRC each pack.
        var result = new byte[packs.Count * PackSize];
        for (int i = 0; i < packs.Count; i++)
        {
            var pack = packs[i];
            pack[2] = (byte)i;                       // sequence number
            ushort crc = Crc16(pack, 0, 16);
            pack[16] = (byte)(crc >> 8);             // big-endian
            pack[17] = (byte)(crc & 0xFF);
            Array.Copy(pack, 0, result, i * PackSize, PackSize);
        }
        return result;
    }

    /// <summary>
    /// Chop one pack type's per-track strings into 12-byte payload packs.
    /// <paramref name="entries"/> index 0 is the disc/album-level string;
    /// index i is track i's string.
    /// </summary>
    private static void AppendTextPacks(
        List<byte[]> packs, byte packType, IReadOnlyList<string> entries, byte block)
    {
        // Flatten into one NUL-terminated byte stream, remembering which track
        // owns each byte so we can fill the track-number + char-position fields.
        var stream    = new List<byte>();
        var byteOwner = new List<int>();
        for (int t = 0; t < entries.Count; t++)
        {
            foreach (byte b in EncodeLatin1(entries[t]))
            {
                stream.Add(b);
                byteOwner.Add(t);
            }
            stream.Add(0x00);          // NUL terminator belongs to this track
            byteOwner.Add(t);
        }

        for (int pos = 0; pos < stream.Count; pos += 12)
        {
            int len = Math.Min(12, stream.Count - pos);
            int firstTrack = byteOwner[pos];

            // Character position: how many of this track's bytes precede `pos`.
            int charPos = 0;
            for (int p = pos - 1; p >= 0 && byteOwner[p] == firstTrack; p--)
                charPos++;
            if (charPos > 15) charPos = 15;

            var pack = new byte[PackSize];
            pack[0] = packType;
            pack[1] = (byte)firstTrack;
            pack[2] = 0;                                                  // sequence — filled by Build
            pack[3] = (byte)(((block & 0x07) << 4) | (charPos & 0x0F));   // single-byte charset
            for (int k = 0; k < len; k++)
                pack[4 + k] = stream[pos + k];
            // bytes 4+len..15 stay zero-padded
            packs.Add(pack);
        }
    }

    /// <summary>
    /// Build the 36-byte size-information record that the three 0x8F packs carry
    /// (12 payload bytes each). Layout per the CD-Text spec.
    /// </summary>
    private static byte[] BuildSizeInfoRecord(
        byte charCode, int firstTrack, int lastTrack,
        IReadOnlyDictionary<byte, int> packCounts, int lastSequenceBlock0)
    {
        var rec = new byte[36];
        rec[0] = charCode;                 // 0x00 = ISO-8859-1
        rec[1] = (byte)firstTrack;
        rec[2] = (byte)lastTrack;
        rec[3] = 0x00;                     // copyright flags — not copyrighted

        // Bytes 4-19: count of packs of type 0x80..0x8F, one byte each.
        for (int t = 0; t < 16; t++)
            rec[4 + t] = (byte)packCounts.GetValueOrDefault((byte)(0x80 + t), 0);

        // Bytes 20-27: last (highest) sequence number used in blocks 0-7.
        rec[20] = (byte)lastSequenceBlock0;
        // blocks 1-7 unused → 0

        // Bytes 28-35: language code per block. Only block 0 is in use.
        rec[28] = LanguageEnglish;
        // blocks 1-7 unused → 0

        return rec;
    }

    /// <summary>
    /// Latin-1 encode a string for a CD-Text payload: trims to the field cap and
    /// replaces control characters (which would corrupt the NUL-delimited stream)
    /// with spaces. Characters outside Latin-1 become '?'.
    /// </summary>
    private static byte[] EncodeLatin1(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        if (s.Length > MaxStringLength) s = s.Substring(0, MaxStringLength);
        var bytes = Encoding.Latin1.GetBytes(s);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] < 0x20) bytes[i] = (byte)' ';
        return bytes;
    }

    /// <summary>
    /// CD-Text CRC-16: polynomial 0x1021, initial value 0x0000, no reflection,
    /// computed over the first 16 bytes of a pack, then bit-inverted. Stored
    /// big-endian in pack bytes 16-17. Verified against cdrdao's CdTextEncoder.
    /// </summary>
    public static ushort Crc16(byte[] data, int offset, int length)
    {
        ushort crc = 0x0000;
        for (int i = 0; i < length; i++)
        {
            crc ^= (ushort)(data[offset + i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
        }
        return (ushort)~crc;
    }

    /// <summary>True if a single 18-byte pack's stored CRC matches its contents.</summary>
    public static bool VerifyPackCrc(byte[] packs, int packOffset)
    {
        ushort want = Crc16(packs, packOffset, 16);
        ushort got  = (ushort)((packs[packOffset + 16] << 8) | packs[packOffset + 17]);
        return want == got;
    }

    /// <summary>
    /// Expand 18-byte packs to the 6-bit-per-byte form the drive writes into the
    /// R-W subchannel: every 3 input bytes become 4 output bytes using only the
    /// low 6 bits of each. An 18-byte pack becomes 24 bytes. This is the exact
    /// transform libburn's burn_write_leadin_cdtext() applies.
    /// </summary>
    public static byte[] ExpandToSubchannel(byte[] packs)
    {
        if (packs.Length % PackSize != 0)
            throw new ArgumentException("Pack stream length must be a multiple of 18", nameof(packs));

        var sub = new byte[packs.Length / PackSize * 24];
        for (int i = 0; i + 2 < packs.Length; i += 3)
        {
            int si = i / 3 * 4;
            sub[si + 0] = (byte)((packs[i + 0] >> 2) & 0x3F);
            sub[si + 1] = (byte)((packs[i + 0] << 4) & 0x30);
            sub[si + 1] |= (byte)((packs[i + 1] >> 4) & 0x0F);
            sub[si + 2] = (byte)((packs[i + 1] << 2) & 0x3C);
            sub[si + 2] |= (byte)((packs[i + 2] >> 6) & 0x03);
            sub[si + 3] = (byte)((packs[i + 2] >> 0) & 0x3F);
        }
        return sub;
    }

    /// <summary>Number of 18-byte packs in a stream.</summary>
    public static int PackCount(byte[] packs) => packs.Length / PackSize;

    /// <summary>One CD-Text lead-in sector holds four 24-byte expanded packs.</summary>
    public const int LeadInSectorBytes = 96;

    /// <summary>
    /// Build the full CD-Text lead-in image to WRITE into the disc lead-in.
    /// The drive expects the entire lead-in filled, so the (expanded) packs are
    /// cycled repeatedly to cover all <paramref name="leadInSectors"/> sectors —
    /// 96 bytes (four expanded packs) per sector. This mirrors libburn's
    /// burn_write_leadin_cdtext(), which advances one global pack cursor and
    /// wraps it modulo the pack count.
    /// </summary>
    public static byte[] BuildLeadInImage(byte[] packs, int leadInSectors)
    {
        if (leadInSectors <= 0)
            throw new ArgumentException("Lead-in must be at least one sector", nameof(leadInSectors));
        int numPacks = PackCount(packs);
        if (numPacks == 0)
            throw new ArgumentException("No CD-Text packs to write", nameof(packs));

        byte[] sub = ExpandToSubchannel(packs);   // 24 bytes per pack
        var image = new byte[leadInSectors * LeadInSectorBytes];
        int cursor = 0;
        for (int sector = 0; sector < leadInSectors; sector++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                Array.Copy(sub, cursor * 24, image, sector * LeadInSectorBytes + slot * 24, 24);
                cursor = (cursor + 1) % numPacks;
            }
        }
        return image;
    }

    /// <summary>Pretty-print the pack stream for debugging / burn logs.</summary>
    public static string Dump(byte[] packs)
    {
        var sb = new StringBuilder();
        int n = PackCount(packs);
        sb.AppendLine($"CD-Text ({packs.Length} bytes, {n} packs):");
        sb.AppendLine("  #   type        trk seq cpos  text                          crc");
        for (int i = 0; i < n; i++)
        {
            int o = i * PackSize;
            byte type = packs[o];
            string typeName = type switch
            {
                PackTitle     => "TITLE",
                PackPerformer => "PERFORMER",
                PackSizeInfo  => "SIZE_INFO",
                _             => $"0x{type:X2}",
            };
            var text = new StringBuilder();
            for (int k = 4; k < 16; k++)
            {
                byte b = packs[o + k];
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : (b == 0 ? '.' : '?'));
            }
            bool crcOk = VerifyPackCrc(packs, o);
            sb.AppendFormat("  {0,-3} {1,-11} {2,3} {3,3} {4,4}  {5,-28}  {6:X2}{7:X2} {8}\n",
                i, typeName, packs[o + 1], packs[o + 2], packs[o + 3] & 0x0F,
                text, packs[o + 16], packs[o + 17], crcOk ? "ok" : "BAD");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decode a pack stream back into disc metadata. Used by tests and the
    /// offline `cdtext-dump` verification path. Only TITLE and PERFORMER are
    /// reconstructed; other pack types are ignored.
    /// </summary>
    public static Disc Decode(byte[] packs)
    {
        var titles     = DecodeStrings(packs, PackTitle);
        var performers = DecodeStrings(packs, PackPerformer);

        int trackCount = Math.Max(
            titles.Count > 0 ? titles.Count - 1 : 0,
            performers.Count > 0 ? performers.Count - 1 : 0);

        string? At(IReadOnlyList<string> xs, int i) => i < xs.Count ? xs[i] : null;

        var tracks = new List<Track>();
        for (int i = 1; i <= trackCount; i++)
            tracks.Add(new Track(At(titles, i), At(performers, i)));

        return new Disc(At(titles, 0), At(performers, 0), tracks);
    }

    /// <summary>
    /// Rebuild the per-track string list for one pack type. Trailing empty
    /// strings are dropped, since they're indistinguishable from the last
    /// pack's zero padding — a debug/verification helper, not a burn path.
    /// </summary>
    private static List<string> DecodeStrings(byte[] packs, byte packType)
    {
        var bytes = new List<byte>();
        int n = PackCount(packs);
        for (int i = 0; i < n; i++)
        {
            int o = i * PackSize;
            if (packs[o] != packType) continue;
            for (int k = 4; k < 16; k++)
                bytes.Add(packs[o + k]);
        }

        // Split on NUL terminators.
        var result = new List<string>();
        var cur = new List<byte>();
        foreach (byte b in bytes)
        {
            if (b == 0x00)
            {
                result.Add(Encoding.Latin1.GetString(cur.ToArray()));
                cur.Clear();
            }
            else
            {
                cur.Add(b);
            }
        }

        // Drop trailing empties left by the final pack's zero padding.
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }
}
