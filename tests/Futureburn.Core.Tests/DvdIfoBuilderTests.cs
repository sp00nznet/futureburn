using System.Text;
using Futureburn.Core.Authoring;

namespace Futureburn.Core.Tests;

public class DvdIfoBuilderTests
{
    [Fact]
    public void Vmg_HasCorrectSignatureAndLength()
    {
        var p = DvdIfoBuilder.BuildVmgIfo();
        Assert.Equal(2048, p.Length);
        Assert.Equal("DVDVIDEO-VMG", Encoding.ASCII.GetString(p, 0, 12));
    }

    [Fact]
    public void Vts_HasCorrectSignatureAndLength()
    {
        var p = DvdIfoBuilder.BuildVtsIfo();
        Assert.Equal(2048, p.Length);
        Assert.Equal("DVDVIDEO-VTS", Encoding.ASCII.GetString(p, 0, 12));
    }

    [Fact]
    public void Vmg_DeclaresSpecVersion10()
    {
        var p = DvdIfoBuilder.BuildVmgIfo();
        Assert.Equal(0x10, p[33]);
    }

    [Fact]
    public void Vmg_VolumeAndSideFieldsSetToOne()
    {
        var p = DvdIfoBuilder.BuildVmgIfo();
        Assert.Equal(0x01, p[39]);  // number of volumes
        Assert.Equal(0x01, p[41]);  // this volume number
        Assert.Equal(0x01, p[42]);  // disc side
    }

    [Fact]
    public void Vmg_NumTitleSetsBigEndian()
    {
        var p = DvdIfoBuilder.BuildVmgIfo(numTitleSets: 5);
        Assert.Equal(0x00, p[62]);
        Assert.Equal(0x05, p[63]);
    }

    [Fact]
    public void Vmg_ProviderIdAsciiUppercaseAndPaddedTo32()
    {
        var p = DvdIfoBuilder.BuildVmgIfo(providerId: "myDisc");
        var s = Encoding.ASCII.GetString(p, 64, 32);
        Assert.Equal(32, s.Length);
        Assert.Equal("MYDISC                          ", s);
    }
}
