using System.Text;
using Futureburn.Core.Authoring;

namespace Futureburn.Core.Tests;

public class VcdBuilderTests
{
    [Fact]
    public void InfoVcd_HasCorrectSignatureAndLength()
    {
        var p = VcdInfoBuilder.Build("MyAlbum", palMode: false);
        Assert.Equal(2048, p.Length);
        var sig = Encoding.ASCII.GetString(p, 0, 8);
        Assert.Equal("VIDEO_CD", sig);
    }

    [Fact]
    public void InfoVcd_WritesProfileAndVersion()
    {
        var p = VcdInfoBuilder.Build("X", palMode: false, systemProfile: 3);
        Assert.Equal(0x03, p[8]);  // System Profile Tag
        Assert.Equal(0x02, p[9]);  // Album Version (BCD)
    }

    [Fact]
    public void InfoVcd_AlbumLabelIsAsciiUppercaseAndPaddedTo16()
    {
        var p = VcdInfoBuilder.Build("hello", palMode: false);
        var album = Encoding.ASCII.GetString(p, 10, 16);
        Assert.Equal("HELLO           ", album);
    }

    [Fact]
    public void InfoVcd_LongLabelTruncatedTo16Chars()
    {
        var p = VcdInfoBuilder.Build("ThisAlbumNameIsWayTooLong", palMode: false);
        var album = Encoding.ASCII.GetString(p, 10, 16);
        Assert.Equal(16, album.Length);
        Assert.Equal("THISALBUMNAMEISW", album);
    }

    [Fact]
    public void InfoVcd_VolumeCountAndNumberAreBigEndian()
    {
        var p = VcdInfoBuilder.Build("X", palMode: false, volumeCount: 3, volumeNumber: 1);
        Assert.Equal(0x00, p[26]); Assert.Equal(0x03, p[27]);   // count = 3
        Assert.Equal(0x00, p[28]); Assert.Equal(0x01, p[29]);   // number = 1
    }

    [Fact]
    public void InfoVcd_PalFlagBit()
    {
        Assert.Equal(0x00, VcdInfoBuilder.Build("X", palMode: false)[30]);
        Assert.Equal(0x80, VcdInfoBuilder.Build("X", palMode: true)[30]);
    }

    [Fact]
    public void EntriesVcd_HasCorrectSignatureAndLength()
    {
        var p = VcdEntriesBuilder.Build(new[]
        {
            new VcdEntriesBuilder.TrackEntry(MmcTrackNumber: 2, StartLba: 0)
        });
        Assert.Equal(2048, p.Length);
        Assert.Equal("ENTRYVCD", Encoding.ASCII.GetString(p, 0, 8));
        Assert.Equal(0x02, p[8]);  // version
    }

    [Fact]
    public void EntriesVcd_NumberOfEntriesIsBigEndian()
    {
        var p = VcdEntriesBuilder.Build(new[]
        {
            new VcdEntriesBuilder.TrackEntry(2, 0),
            new VcdEntriesBuilder.TrackEntry(3, 1000),
            new VcdEntriesBuilder.TrackEntry(4, 2000),
        });
        // bytes 10-11 are the BE16 count
        Assert.Equal(0x00, p[10]);
        Assert.Equal(0x03, p[11]);
    }

    [Fact]
    public void EntriesVcd_FirstEntryEncodesTrackAndMsfBcd()
    {
        // LBA 0 → abs 150 frames → MSF 00:02:00 (after the 150-frame lead-in)
        var p = VcdEntriesBuilder.Build(new[]
        {
            new VcdEntriesBuilder.TrackEntry(MmcTrackNumber: 2, StartLba: 0),
        });
        // First entry begins at offset 12.
        Assert.Equal(0x02, p[12]);            // track number BCD = 02
        Assert.Equal(0x00, p[13]);            // M = 0
        Assert.Equal(0x02, p[14]);            // S = 2 (BCD)
        Assert.Equal(0x00, p[15]);            // F = 0
        // Reserved bytes 16-21 should be zero.
        for (int i = 16; i < 22; i++) Assert.Equal(0x00, p[i]);
    }

    [Theory]
    [InlineData(0,  0)]
    [InlineData(1,  1)]
    [InlineData(9,  9)]
    [InlineData(10, 0x10)]
    [InlineData(23, 0x23)]
    [InlineData(99, 0x99)]
    public void ToBcd_EncodesCorrectly(int decimalValue, int bcdByte)
    {
        Assert.Equal((byte)bcdByte, VcdEntriesBuilder.ToBcd(decimalValue));
    }

    [Theory]
    [InlineData(0,    0x00, 0x02, 0x00)]   // LBA 0 = MSF 00:02:00
    [InlineData(75,   0x00, 0x03, 0x00)]   // LBA 75 = MSF 00:03:00 (1 sec later)
    [InlineData(4500, 0x01, 0x02, 0x00)]   // LBA 4500 = MSF 01:02:00 (60 sec later)
    public void LbaToMsfBcd_ConvertsCorrectly(long lba, byte expM, byte expS, byte expF)
    {
        var (m, s, f) = VcdEntriesBuilder.LbaToMsfBcd(lba);
        Assert.Equal(expM, m);
        Assert.Equal(expS, s);
        Assert.Equal(expF, f);
    }
}
