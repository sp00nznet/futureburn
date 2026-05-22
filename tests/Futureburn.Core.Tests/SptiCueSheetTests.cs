using Futureburn.Core.Spti;

namespace Futureburn.Core.Tests;

// The SEND CUE SHEET parameter list is plain BINARY, has no A0/A1/A2 pointer
// descriptors, and uses DATA FORM 0x01 on the lead-in/lead-out entries. These
// tests pin that format down — an earlier BCD-encoded version was rejected by
// real drives with sense 0x5/0x26/0x00.
public class SptiCueSheetTests
{
    private static byte[] Descriptor(byte[] cue, int index)
        => cue.Skip(index * 8).Take(8).ToArray();

    // ---- LbaToMsf (binary) ------------------------------------------------

    [Theory]
    [InlineData(-150, 0,  0,  0)]    // pre-lead-in
    [InlineData(0,    0,  2,  0)]    // LBA 0 = MSF 00:02:00
    [InlineData(75,   0,  3,  0)]    // 1 second of data
    [InlineData(150,  0,  4,  0)]    // 2 seconds
    public void LbaToMsf_ConvertsCorrectly(long lba, byte m, byte s, byte f)
    {
        var (gm, gs, gf) = SptiCueSheet.LbaToMsf(lba);
        Assert.Equal(m, gm);
        Assert.Equal(s, gs);
        Assert.Equal(f, gf);
    }

    [Fact]
    public void LbaToMsf_IsBinaryNotBcd()
    {
        // 23 min 45 sec → binary 23, 45 (NOT BCD 0x23, 0x45).
        // LBA: 23*60*75 + 45*75 - 150 = 103500 + 3375 - 150 = 106725.
        var (m, s, f) = SptiCueSheet.LbaToMsf(106725);
        Assert.Equal(23, m);
        Assert.Equal(45, s);
        Assert.Equal(0,  f);
    }

    // ---- Cue sheet structure ---------------------------------------------

    [Fact]
    public void BuildAudioCd_HasLeadInPerTrackAndLeadOut()
    {
        // 1 lead-in + 2 per track + 1 lead-out.
        var tracks = Enumerable.Range(1, 5)
            .Select(_ => new SptiCueSheet.Track(4500)).ToArray();
        var cue = SptiCueSheet.BuildAudioCd(tracks, gapless: true);
        Assert.Equal((2 + 5 * 2) * 8, cue.Length);
    }

    [Fact]
    public void BuildAudioCd_FirstDescriptorIsTheLeadIn()
    {
        var cue = SptiCueSheet.BuildAudioCd(new[] { new SptiCueSheet.Track(4500) });
        var d = Descriptor(cue, 0);
        Assert.Equal(0x01, d[0]);   // CTL|ADR
        Assert.Equal(0x00, d[1]);   // TNO 0 = lead-in
        Assert.Equal(0x00, d[2]);   // INDEX 0
        Assert.Equal(0x01, d[3]);   // DATA FORM 0x01 = audio pause
        Assert.Equal(0x00, d[5]);   // MSF 00:00:00
        Assert.Equal(0x00, d[6]);
        Assert.Equal(0x00, d[7]);
    }

    [Fact]
    public void BuildAudioCd_LastDescriptorIsTheLeadOut()
    {
        var cue = SptiCueSheet.BuildAudioCd(new[] { new SptiCueSheet.Track(4500) });
        var d = Descriptor(cue, cue.Length / 8 - 1);
        Assert.Equal(0xAA, d[1]);   // TNO 0xAA = lead-out
        Assert.Equal(0x01, d[2]);   // INDEX 1
        Assert.Equal(0x01, d[3]);   // DATA FORM 0x01 = audio pause
    }

    [Fact]
    public void BuildAudioCd_HasNoA0A1A2PointerDescriptors()
    {
        var tracks = Enumerable.Range(1, 12)
            .Select(_ => new SptiCueSheet.Track(4500)).ToArray();
        var cue = SptiCueSheet.BuildAudioCd(tracks);
        for (int i = 0; i < cue.Length; i += 8)
            Assert.True(cue[i + 2] is not (0xA0 or 0xA1 or 0xA2),
                        $"descriptor {i / 8} is an A0/A1/A2 pointer — those don't belong in a cue sheet");
    }

    [Fact]
    public void BuildAudioCd_TrackNumbersAreBinary()
    {
        // 19 tracks: the cue sheet must carry binary track numbers, so track
        // 19's descriptors hold byte 19 (0x13) — not BCD 0x19.
        var tracks = Enumerable.Range(1, 19)
            .Select(_ => new SptiCueSheet.Track(4500)).ToArray();
        var cue = SptiCueSheet.BuildAudioCd(tracks);

        // Layout: descriptor 0 = lead-in; track N's INDEX 0 is at 1 + (N-1)*2.
        Assert.Equal(10, Descriptor(cue, 1 + (10 - 1) * 2)[1]);   // track 10 → 10
        Assert.Equal(19, Descriptor(cue, 1 + (19 - 1) * 2)[1]);   // track 19 → 19
        Assert.Equal(19, Descriptor(cue, 1 + (19 - 1) * 2 + 1)[1]);
    }

    [Fact]
    public void BuildAudioCd_AudioPayloadEntriesUseDataForm00()
    {
        var cue = SptiCueSheet.BuildAudioCd(new[]
        {
            new SptiCueSheet.Track(4500), new SptiCueSheet.Track(4500),
        });
        // Track descriptors (between lead-in at 0 and lead-out at the end).
        for (int i = 1; i < cue.Length / 8 - 1; i++)
            Assert.Equal(0x00, Descriptor(cue, i)[3]);
        // Every descriptor's CTL|ADR byte is 0x01 (audio).
        for (int i = 0; i < cue.Length; i += 8)
            Assert.Equal(0x01, cue[i]);
    }

    // ---- CD-Text flag -----------------------------------------------------

    [Fact]
    public void BuildAudioCd_CdTextSetsDataForm41OnLeadInOnly()
    {
        var cue = SptiCueSheet.BuildAudioCd(
            new[] { new SptiCueSheet.Track(4500), new SptiCueSheet.Track(4500) },
            gapless: true, cdText: true);
        Assert.Equal(0x41, Descriptor(cue, 0)[3]);                  // lead-in
        Assert.Equal(0x01, Descriptor(cue, cue.Length / 8 - 1)[3]); // lead-out unchanged
    }

    [Fact]
    public void BuildAudioCd_NoCdTextLeadInIsPlainAudioPause()
    {
        var cue = SptiCueSheet.BuildAudioCd(
            new[] { new SptiCueSheet.Track(4500) }, gapless: true, cdText: false);
        Assert.Equal(0x01, Descriptor(cue, 0)[3]);
    }

    // ---- Gapless vs not ---------------------------------------------------

    [Fact]
    public void BuildAudioCd_GaplessTrack2Index0EqualsIndex1()
    {
        var cue = SptiCueSheet.BuildAudioCd(new[]
        {
            new SptiCueSheet.Track(4500), new SptiCueSheet.Track(4500),
        }, gapless: true);
        // Track 2: INDEX 0 at descriptor 3, INDEX 1 at descriptor 4.
        var idx0 = Descriptor(cue, 3);
        var idx1 = Descriptor(cue, 4);
        Assert.Equal(idx1[5], idx0[5]);
        Assert.Equal(idx1[6], idx0[6]);
        Assert.Equal(idx1[7], idx0[7]);
    }

    [Fact]
    public void BuildAudioCd_NonGaplessPushesTheLeadOutLater()
    {
        var tracks = new[] { new SptiCueSheet.Track(4500), new SptiCueSheet.Track(4500) };
        var gapless    = SptiCueSheet.BuildAudioCd(tracks, gapless: true);
        var nonGapless = SptiCueSheet.BuildAudioCd(tracks, gapless: false);
        // Same descriptor count; the inter-track gap moves the lead-out.
        Assert.Equal(gapless.Length, nonGapless.Length);
        var gLeadOut  = Descriptor(gapless,    gapless.Length / 8 - 1);
        var ngLeadOut = Descriptor(nonGapless, nonGapless.Length / 8 - 1);
        Assert.True(ngLeadOut[6] != gLeadOut[6] || ngLeadOut[7] != gLeadOut[7]);
    }

    // ---- Byte-level worked example (from the MMC research) ----------------

    [Fact]
    public void BuildAudioCd_ThreeTracks_MatchesWorkedExample()
    {
        // Tracks of 10, 12, 8 sectors → starts LBA 0, 10, 22; lead-out LBA 30.
        var cue = SptiCueSheet.BuildAudioCd(new[]
        {
            new SptiCueSheet.Track(10), new SptiCueSheet.Track(12), new SptiCueSheet.Track(8),
        }, gapless: true);

        byte[][] expected =
        {
            new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 },  // lead-in
            new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00 },  // T1 idx0
            new byte[] { 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00 },  // T1 idx1
            new byte[] { 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x02, 0x0A },  // T2 idx0 (LBA 10)
            new byte[] { 0x01, 0x02, 0x01, 0x00, 0x00, 0x00, 0x02, 0x0A },  // T2 idx1
            new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x02, 0x16 },  // T3 idx0 (LBA 22)
            new byte[] { 0x01, 0x03, 0x01, 0x00, 0x00, 0x00, 0x02, 0x16 },  // T3 idx1
            new byte[] { 0x01, 0xAA, 0x01, 0x01, 0x00, 0x00, 0x02, 0x1E },  // lead-out (LBA 30)
        };

        Assert.Equal(expected.Length * 8, cue.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Descriptor(cue, i));
    }

    // ---- Guards ----------------------------------------------------------

    [Fact]
    public void BuildAudioCd_RejectsZeroTracks()
        => Assert.Throws<ArgumentException>(() =>
            SptiCueSheet.BuildAudioCd(Array.Empty<SptiCueSheet.Track>()));

    [Fact]
    public void BuildAudioCd_Rejects100Tracks()
    {
        var tracks = Enumerable.Range(1, 100)
            .Select(_ => new SptiCueSheet.Track(100)).ToArray();
        Assert.Throws<ArgumentException>(() => SptiCueSheet.BuildAudioCd(tracks));
    }
}
