using System.Text;
using Futureburn.Core.Spti;

namespace Futureburn.Core.Tests;

public class SptiCdTextTests
{
    // The worked example used throughout: album "TEST ALBUM" by "TEST ARTIST",
    // two tracks "SONG ONE" / "SONG TWO". Cross-checked against the CD-Text spec.
    private static SptiCdText.Disc SampleDisc() => new(
        AlbumTitle:     "TEST ALBUM",
        AlbumPerformer: "TEST ARTIST",
        Tracks: new[]
        {
            new SptiCdText.Track("SONG ONE", null),
            new SptiCdText.Track("SONG TWO", null),
        });

    private static byte[] Pack(byte[] packs, int index) =>
        packs.Skip(index * SptiCdText.PackSize).Take(SptiCdText.PackSize).ToArray();

    // ---- CRC-16 ------------------------------------------------------------

    [Fact]
    public void Crc16_MatchesCatalogedKnownAnswer()
    {
        // CD-Text CRC = CRC-16/XMODEM (poly 0x1021, init 0) then bit-inverted.
        // CRC-16/XMODEM("123456789") is the cataloged check value 0x31C3,
        // so the CD-Text CRC is its complement: 0xCE3C.
        var data = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCE3C, SptiCdText.Crc16(data, 0, data.Length));
    }

    [Fact]
    public void Crc16_AllZeroInputIsInverted()
    {
        // 16 zero bytes leave the register at 0; the final inversion makes 0xFFFF.
        Assert.Equal(0xFFFF, SptiCdText.Crc16(new byte[16], 0, 16));
    }

    [Fact]
    public void Build_EveryPackHasValidCrc()
    {
        var packs = SptiCdText.Build(SampleDisc());
        for (int i = 0; i < SptiCdText.PackCount(packs); i++)
            Assert.True(SptiCdText.VerifyPackCrc(packs, i * SptiCdText.PackSize),
                        $"pack {i} CRC invalid");
    }

    // ---- Pack stream structure --------------------------------------------

    [Fact]
    public void Build_LengthIsMultipleOf18()
    {
        var packs = SptiCdText.Build(SampleDisc());
        Assert.Equal(0, packs.Length % SptiCdText.PackSize);
    }

    [Fact]
    public void Build_SampleDiscProducesNinePacks()
    {
        // 3 TITLE (29 text bytes) + 3 PERFORMER (36 text bytes) + 3 SIZE_INFO.
        var packs = SptiCdText.Build(SampleDisc());
        Assert.Equal(9, SptiCdText.PackCount(packs));
    }

    [Fact]
    public void Build_SequenceNumbersAreContiguousFromZero()
    {
        var packs = SptiCdText.Build(SampleDisc());
        for (int i = 0; i < SptiCdText.PackCount(packs); i++)
            Assert.Equal(i, packs[i * SptiCdText.PackSize + 2]);
    }

    [Fact]
    public void Build_PackTypeOrderIsTitleThenPerformerThenSizeInfo()
    {
        var packs = SptiCdText.Build(SampleDisc());
        byte[] types = Enumerable.Range(0, SptiCdText.PackCount(packs))
                                 .Select(i => packs[i * SptiCdText.PackSize])
                                 .ToArray();
        Assert.Equal(new byte[]
        {
            0x80, 0x80, 0x80,   // TITLE
            0x81, 0x81, 0x81,   // PERFORMER
            0x8F, 0x8F, 0x8F,   // SIZE_INFO
        }, types);
    }

    // ---- TITLE packs: exact byte-level worked example ----------------------

    [Fact]
    public void Build_TitlePacksMatchWorkedExample()
    {
        var packs = SptiCdText.Build(SampleDisc());

        // Pack 0: type 0x80, track 0, char-pos 0, "TEST ALBUM\0S".
        var p0 = Pack(packs, 0);
        Assert.Equal(0x80, p0[0]);
        Assert.Equal(0x00, p0[1]);                       // track 0 (album)
        Assert.Equal(0x00, p0[3] & 0x0F);                // char position
        Assert.Equal(Encoding.ASCII.GetBytes("TEST ALBUM"),
                     p0.Skip(4).Take(10).ToArray());
        Assert.Equal(0x00, p0[14]);                      // NUL terminator
        Assert.Equal((byte)'S', p0[15]);                 // head of next string

        // Pack 1: type 0x80, track 1, char-pos 1 ("S" of "SONG ONE" was in p0).
        var p1 = Pack(packs, 1);
        Assert.Equal(0x80, p1[0]);
        Assert.Equal(0x01, p1[1]);
        Assert.Equal(0x01, p1[3] & 0x0F);
        Assert.Equal(Encoding.ASCII.GetBytes("ONG ONE"),
                     p1.Skip(4).Take(7).ToArray());

        // Pack 2: type 0x80, track 2, char-pos 4 ("SONG" was in p1).
        var p2 = Pack(packs, 2);
        Assert.Equal(0x80, p2[0]);
        Assert.Equal(0x02, p2[1]);
        Assert.Equal(0x04, p2[3] & 0x0F);
        Assert.Equal(Encoding.ASCII.GetBytes(" TWO"),
                     p2.Skip(4).Take(4).ToArray());
        Assert.Equal(0x00, p2[8]);                       // NUL, then zero padding
    }

    [Fact]
    public void Build_TracksWithoutPerformerInheritAlbumArtist()
    {
        // The sample tracks have null performers; each PERFORMER pack should
        // still carry "TEST ARTIST".
        var packs = SptiCdText.Build(SampleDisc());
        var p3 = Pack(packs, 3);
        Assert.Equal(0x81, p3[0]);
        Assert.Equal(Encoding.ASCII.GetBytes("TEST ARTIST"),
                     p3.Skip(4).Take(11).ToArray());
    }

    // ---- SIZE_INFO (0x8F) packs -------------------------------------------

    [Fact]
    public void Build_SizeInfoRecordHasCorrectCountsAndRange()
    {
        var packs = SptiCdText.Build(SampleDisc());

        // Reassemble the 36-byte size record from the three 0x8F payloads.
        var record = new byte[36];
        for (int i = 0; i < 3; i++)
            Array.Copy(Pack(packs, 6 + i), 4, record, i * 12, 12);

        Assert.Equal(0x00, record[0]);   // char code = ISO-8859-1
        Assert.Equal(0x01, record[1]);   // first track
        Assert.Equal(0x02, record[2]);   // last track
        Assert.Equal(0x00, record[3]);   // not copyrighted
        Assert.Equal(3,    record[4]);   // three 0x80 TITLE packs
        Assert.Equal(3,    record[5]);   // three 0x81 PERFORMER packs
        Assert.Equal(3,    record[19]);  // three 0x8F SIZE_INFO packs (self-counted)
        Assert.Equal(8,    record[20]);  // highest sequence number in block 0
        Assert.Equal(0x09, record[28]);  // block 0 language = English
    }

    // ---- 6-bit subchannel expansion ---------------------------------------

    [Fact]
    public void ExpandToSubchannel_GrowsEachPackFrom18To24Bytes()
    {
        var packs = SptiCdText.Build(SampleDisc());
        var sub   = SptiCdText.ExpandToSubchannel(packs);
        Assert.Equal(SptiCdText.PackCount(packs) * 24, sub.Length);
    }

    [Fact]
    public void ExpandToSubchannel_MatchesLibburnWorkedExample()
    {
        // From libburn's burn_write_leadin_cdtext() worked example:
        // an 18-byte pack expands to a specific 24-byte form.
        byte[] pack =
        {
            0x8F, 0x00, 0x2A, 0x00, 0x01, 0x01, 0x03, 0x00, 0x06,
            0x05, 0x04, 0x05, 0x07, 0x06, 0x01, 0x02, 0x48, 0x65,
        };
        byte[] expected =
        {
            0x23, 0x30, 0x00, 0x2A, 0x00, 0x00, 0x04, 0x01,
            0x00, 0x30, 0x00, 0x06, 0x01, 0x10, 0x10, 0x05,
            0x01, 0x30, 0x18, 0x01, 0x00, 0x24, 0x21, 0x25,
        };
        Assert.Equal(expected, SptiCdText.ExpandToSubchannel(pack));
    }

    [Fact]
    public void ExpandToSubchannel_UsesOnlyLowSixBits()
    {
        var sub = SptiCdText.ExpandToSubchannel(SptiCdText.Build(SampleDisc()));
        Assert.All(sub, b => Assert.True(b <= 0x3F, $"byte 0x{b:X2} exceeds 6 bits"));
    }

    // ---- Round-trip --------------------------------------------------------

    [Fact]
    public void Decode_RoundTripsTitlesAndPerformers()
    {
        var original = new SptiCdText.Disc(
            AlbumTitle:     "Greatest Hits",
            AlbumPerformer: "The Band",
            Tracks: new[]
            {
                new SptiCdText.Track("Opening Number", "The Band"),
                new SptiCdText.Track("A Much Longer Track Title Than Twelve", "Guest Star"),
                new SptiCdText.Track("Finale", "The Band"),
            });

        var decoded = SptiCdText.Decode(SptiCdText.Build(original));

        Assert.Equal(original.AlbumTitle, decoded.AlbumTitle);
        Assert.Equal(original.AlbumPerformer, decoded.AlbumPerformer);
        Assert.Equal(original.Tracks.Count, decoded.Tracks.Count);
        for (int i = 0; i < original.Tracks.Count; i++)
        {
            Assert.Equal(original.Tracks[i].Title, decoded.Tracks[i].Title);
            Assert.Equal(original.Tracks[i].Performer, decoded.Tracks[i].Performer);
        }
    }

    // ---- Guards ------------------------------------------------------------

    [Fact]
    public void Build_RejectsZeroTracks()
    {
        Assert.Throws<ArgumentException>(() =>
            SptiCdText.Build(new SptiCdText.Disc("Album", "Artist",
                                                 Array.Empty<SptiCdText.Track>())));
    }

    [Fact]
    public void Build_RejectsDiscWithNoMetadata()
    {
        var disc = new SptiCdText.Disc(null, null, new[]
        {
            new SptiCdText.Track(null, null),
        });
        Assert.False(disc.HasAnything);
        Assert.Throws<ArgumentException>(() => SptiCdText.Build(disc));
    }

    [Fact]
    public void Build_TitleOnlyDiscOmitsPerformerPacks()
    {
        var disc = new SptiCdText.Disc("Album", null, new[]
        {
            new SptiCdText.Track("Track One", null),
        });
        var packs = SptiCdText.Build(disc);
        for (int i = 0; i < SptiCdText.PackCount(packs); i++)
            Assert.NotEqual(0x81, packs[i * SptiCdText.PackSize]);
    }

    // ---- Lead-in image -----------------------------------------------------

    [Fact]
    public void BuildLeadInImage_HasNinetySixBytesPerSector()
    {
        var packs = SptiCdText.Build(SampleDisc());
        var image = SptiCdText.BuildLeadInImage(packs, leadInSectors: 100);
        Assert.Equal(100 * 96, image.Length);
    }

    [Fact]
    public void BuildLeadInImage_FirstSectorIsFirstFourExpandedPacks()
    {
        var packs = SptiCdText.Build(SampleDisc());
        var sub   = SptiCdText.ExpandToSubchannel(packs);
        var image = SptiCdText.BuildLeadInImage(packs, leadInSectors: 50);

        // One sector = four 24-byte packs = the first 96 expanded bytes.
        Assert.Equal(sub.Take(96).ToArray(), image.Take(96).ToArray());
    }

    [Fact]
    public void BuildLeadInImage_CyclesPacksToFillEntireLeadIn()
    {
        var packs    = SptiCdText.Build(SampleDisc());   // 9 packs
        int numPacks = SptiCdText.PackCount(packs);
        var sub      = SptiCdText.ExpandToSubchannel(packs);

        // Enough sectors that the 9-pack cycle wraps several times.
        var image = SptiCdText.BuildLeadInImage(packs, leadInSectors: 30);

        // Every 24-byte slot in the image must equal some expanded pack, and
        // the cursor advances one pack per slot, wrapping modulo numPacks.
        for (int slot = 0; slot < image.Length / 24; slot++)
        {
            int pack = slot % numPacks;
            Assert.Equal(sub.Skip(pack * 24).Take(24).ToArray(),
                         image.Skip(slot * 24).Take(24).ToArray());
        }
    }

    [Fact]
    public void BuildLeadInImage_RejectsZeroSectors()
    {
        var packs = SptiCdText.Build(SampleDisc());
        Assert.Throws<ArgumentException>(() => SptiCdText.BuildLeadInImage(packs, 0));
    }
}
