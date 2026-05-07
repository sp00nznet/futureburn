using Futureburn.Core.Spti;

namespace Futureburn.Core.Tests;

public class SptiCueSheetTests
{
    [Theory]
    [InlineData(0,    0x00, 0x02, 0x00)]   // LBA 0 = MSF 00:02:00 (after 2-sec lead-in offset)
    [InlineData(75,   0x00, 0x03, 0x00)]   // 1 second of data
    [InlineData(150,  0x00, 0x04, 0x00)]   // 2 seconds
    [InlineData(4500, 0x01, 0x02, 0x00)]   // 4500 sectors past lead-in = 1:02 absolute
    [InlineData(-150, 0x00, 0x00, 0x00)]   // pre-lead-in
    public void LbaToMsfBcd_ConvertsCorrectly(long lba, byte expM, byte expS, byte expF)
    {
        var (m, s, f) = SptiCueSheet.LbaToMsfBcd(lba);
        Assert.Equal(expM, m);
        Assert.Equal(expS, s);
        Assert.Equal(expF, f);
    }

    [Fact]
    public void LbaToMsfBcd_BcdEncodesEachField()
    {
        // 23 minutes 45 seconds → 0x23, 0x45 (BCD; not 0x17, 0x2D)
        // Pick an LBA: 23*60*75 + 45*75 - 150 = 103500 + 3375 - 150 = 106725
        var (m, s, f) = SptiCueSheet.LbaToMsfBcd(106725);
        Assert.Equal(0x23, m);
        Assert.Equal(0x45, s);
        Assert.Equal(0x00, f);
    }

    [Fact]
    public void BuildAudioCd_OneTrack_HasA0A1A2PlusTrackEntries()
    {
        var cue = SptiCueSheet.BuildAudioCd(
            new[] { new SptiCueSheet.Track(LengthSectors: 4500) },  // 1 minute
            gapless: true);

        // Expect: A0 + A1 + A2 + (Index 0 + Index 1 for track 1) + lead-out marker = 6 descriptors = 48 bytes
        Assert.Equal(48, cue.Length);

        // Pull out the descriptor types from byte 2 (INDEX field).
        var indexFields = new byte[cue.Length / 8];
        for (int i = 0; i < indexFields.Length; i++)
            indexFields[i] = cue[i * 8 + 2];

        Assert.Equal(0xA0, indexFields[0]);
        Assert.Equal(0xA1, indexFields[1]);
        Assert.Equal(0xA2, indexFields[2]);
        Assert.Equal(0x00, indexFields[3]);  // track 1 pre-gap
        Assert.Equal(0x01, indexFields[4]);  // track 1 body
        Assert.Equal(0x01, indexFields[5]);  // lead-out marker
    }

    [Fact]
    public void BuildAudioCd_FiveTracks_ProducesExpectedDescriptorCount()
    {
        var tracks = Enumerable.Range(1, 5)
            .Select(_ => new SptiCueSheet.Track(LengthSectors: 4500))
            .ToArray();

        var cue = SptiCueSheet.BuildAudioCd(tracks, gapless: true);
        // 3 pointer entries + 2 entries per track + 1 lead-out marker = 14 descriptors
        Assert.Equal(14 * 8, cue.Length);
    }

    [Fact]
    public void BuildAudioCd_GaplessTrack2Index0_EqualsTrack2Index1()
    {
        // For gapless, track N's INDEX 0 and INDEX 1 are at the same MSF
        // (zero-frame pre-gap).
        var tracks = new[]
        {
            new SptiCueSheet.Track(LengthSectors: 4500),
            new SptiCueSheet.Track(LengthSectors: 4500),
        };
        var cue = SptiCueSheet.BuildAudioCd(tracks, gapless: true);

        // Layout: A0(0) A1(1) A2(2) T1Idx0(3) T1Idx1(4) T2Idx0(5) T2Idx1(6) LeadOut(7)
        // Compare bytes 5/6/7 (M/S/F) of T2Idx0 and T2Idx1.
        for (int i = 5; i < 8; i++)
            Assert.Equal(cue[5 * 8 + i], cue[6 * 8 + i]);
    }

    [Fact]
    public void BuildAudioCd_NonGapless_AddsPreGapBetweenTracks()
    {
        var tracks = new[]
        {
            new SptiCueSheet.Track(LengthSectors: 4500),
            new SptiCueSheet.Track(LengthSectors: 4500),
        };
        var gapless    = SptiCueSheet.BuildAudioCd(tracks, gapless: true);
        var nonGapless = SptiCueSheet.BuildAudioCd(tracks, gapless: false);

        // Same descriptor count.
        Assert.Equal(gapless.Length, nonGapless.Length);

        // The lead-out MSF (descriptor 2 = A2) should be 150 sectors later
        // in non-gapless because of the inter-track gap.
        // 150 sectors = 2 sec → MSF byte 6 (S) differs by 0x02 in BCD.
        Assert.NotEqual(gapless[2 * 8 + 6], nonGapless[2 * 8 + 6]);
    }

    [Fact]
    public void BuildAudioCd_AudioTracksCarryCdDaControlByte()
    {
        var cue = SptiCueSheet.BuildAudioCd(
            new[] { new SptiCueSheet.Track(LengthSectors: 4500) },
            gapless: true);

        // Every descriptor's byte 0 should be 0x01 (ADR=0, audio CTL).
        for (int i = 0; i < cue.Length; i += 8)
            Assert.Equal(0x01, cue[i]);
    }

    [Fact]
    public void BuildAudioCd_RejectsZeroTracks()
    {
        Assert.Throws<ArgumentException>(() =>
            SptiCueSheet.BuildAudioCd(Array.Empty<SptiCueSheet.Track>()));
    }

    [Fact]
    public void BuildAudioCd_Rejects100Tracks()
    {
        var tracks = Enumerable.Range(1, 100)
            .Select(_ => new SptiCueSheet.Track(LengthSectors: 100))
            .ToArray();
        Assert.Throws<ArgumentException>(() =>
            SptiCueSheet.BuildAudioCd(tracks));
    }
}
