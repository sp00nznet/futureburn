using Futureburn.Core.Audio;

namespace Futureburn.Core.Tests;

public class CdFormatTests
{
    [Fact]
    public void Constants_MatchRedBookSpec()
    {
        Assert.Equal(44100, CdFormat.SampleRate);
        Assert.Equal(16,    CdFormat.BitsPerSample);
        Assert.Equal(2,     CdFormat.Channels);
        Assert.Equal(4,     CdFormat.BlockAlign);          // 2 ch × 2 bytes
        Assert.Equal(176400, CdFormat.BytesPerSecond);     // 44100 × 4

        Assert.Equal(2352, CdFormat.SectorBytes);          // CD-DA frame
        Assert.Equal(75,   CdFormat.SectorsPerSecond);     // CD audio frames/sec
        Assert.Equal(588,  CdFormat.SamplesPerSector);     // 2352 / 4
    }

    [Theory]
    [InlineData(0,   0)]
    [InlineData(1,   75)]      // 1 sec  = 75 sectors
    [InlineData(60,  4500)]    // 1 min  = 4500 sectors
    [InlineData(3 * 60, 13500)]
    public void SectorsForDuration_ComputesCorrectly(int seconds, long expected)
    {
        Assert.Equal(expected, CdFormat.SectorsForDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0,    0)]
    [InlineData(1,    176400)]      // 1 sec  = 176400 bytes
    [InlineData(60,   60 * 176400)]
    public void BytesForDuration_ComputesCorrectly(int seconds, long expected)
    {
        Assert.Equal(expected, CdFormat.BytesForDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void DiscCapacities_MatchKnownValues()
    {
        // 74-min disc = 74 × 60 × 75 = 333,000 sectors
        Assert.Equal(333000, CdFormat.Sectors74Min);
        // 80-min disc = 80 × 60 × 75 = 360,000 sectors
        Assert.Equal(360000, CdFormat.Sectors80Min);
    }

    [Fact]
    public void SectorsForDuration_RoundsUp()
    {
        // 0.5 sec = 37.5 sectors → rounds up to 38
        Assert.Equal(38, CdFormat.SectorsForDuration(TimeSpan.FromSeconds(0.5)));
    }
}
