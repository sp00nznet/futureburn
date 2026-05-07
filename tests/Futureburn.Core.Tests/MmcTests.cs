using Futureburn.Core.Imapi;

namespace Futureburn.Core.Tests;

public class MmcTests
{
    [Theory]
    [InlineData(0x0009, "CD-R",                 Mmc.ProfileCategory.Cd,    true)]
    [InlineData(0x000A, "CD-RW",                Mmc.ProfileCategory.Cd,    true)]
    [InlineData(0x0008, "CD-ROM",               Mmc.ProfileCategory.Cd,    false)]
    [InlineData(0x0010, "DVD-ROM",              Mmc.ProfileCategory.Dvd,   false)]
    [InlineData(0x001B, "DVD+R",                Mmc.ProfileCategory.Dvd,   true)]
    [InlineData(0x002B, "DVD+R DL",             Mmc.ProfileCategory.Dvd,   true)]
    [InlineData(0x0040, "BD-ROM",               Mmc.ProfileCategory.BluRay, false)]
    [InlineData(0x0043, "BD-RE",                Mmc.ProfileCategory.BluRay, true)]
    [InlineData(0x0050, "HD DVD-ROM",           Mmc.ProfileCategory.HdDvd, false)]
    public void LookupProfile_KnownCodes_ReturnsCorrectInfo(int code, string name, Mmc.ProfileCategory cat, bool writable)
    {
        var p = Mmc.LookupProfile(code);
        Assert.Equal(code, p.Code);
        Assert.Equal(name, p.Name);
        Assert.Equal(cat, p.Category);
        Assert.Equal(writable, p.Writable);
    }

    [Fact]
    public void LookupProfile_UnknownCode_ReturnsHexFormatted()
    {
        var p = Mmc.LookupProfile(0xABCD);
        Assert.Equal(0xABCD, p.Code);
        Assert.Contains("ABCD", p.Name);
    }

    [Theory]
    [InlineData(0x0009, "0x0009")]
    [InlineData(0xABCD, "0xABCD")]
    public void HexCode_FormatsAsFourHexDigits(int code, string expected)
    {
        var p = Mmc.LookupProfile(code);
        Assert.Equal(expected, p.HexCode);
    }

    [Theory]
    [InlineData(0x002D, "CD Track at Once")]
    [InlineData(0x002E, "CD Mastering / Session at Once")]
    [InlineData(0x0021, "Incremental Streaming Writable")]
    public void LookupFeature_KnownCodes_ReturnsCorrectName(int code, string expected)
    {
        var f = Mmc.LookupFeature(code);
        Assert.Equal(expected, f.Name);
    }

    [Theory]
    [InlineData(0x0009, Mmc.MediaPhysicalType.CdR)]
    [InlineData(0x000A, Mmc.MediaPhysicalType.CdRw)]
    [InlineData(0x001B, Mmc.MediaPhysicalType.DvdPlusR)]
    [InlineData(0x0040, Mmc.MediaPhysicalType.BdRom)]
    public void ProfileToMedia_KnownProfiles_MapCorrectly(int profile, Mmc.MediaPhysicalType expected)
    {
        Assert.Equal(expected, Mmc.ProfileToMedia(profile));
    }

    [Fact]
    public void ProfileToMedia_UnknownProfile_ReturnsUnknown()
    {
        Assert.Equal(Mmc.MediaPhysicalType.Unknown, Mmc.ProfileToMedia(0xDEAD));
    }
}
