using Futureburn.Core.Fs;

namespace Futureburn.Core.Tests;

public class DiscFolderValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public DiscFolderValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-validator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    private string Make(string subPath, byte[]? content = null)
    {
        var full = Path.Combine(_tempDir, subPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content ?? Array.Empty<byte>());
        return full;
    }

    [Fact]
    public void Empty_Folder_TypeUnknown_NotWellFormed()
    {
        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.Unknown, v.Type);
        Assert.False(v.LooksWellFormed);
    }

    [Fact]
    public void NonexistentFolder_TypeUnknown_WithIssue()
    {
        var v = DiscFolderValidator.Validate(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Equal(DiscFolderValidator.DiscType.Unknown, v.Type);
        Assert.False(v.LooksWellFormed);
        Assert.NotEmpty(v.Issues);
    }

    [Fact]
    public void PlainFiles_TypeDataDisc_WellFormed()
    {
        Make("readme.txt");
        Make("photos/cat.jpg");
        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DataDisc, v.Type);
        Assert.True(v.LooksWellFormed);
    }

    [Fact]
    public void DvdVideoComplete_TypeDvdVideo_WellFormed()
    {
        Make("VIDEO_TS/VIDEO_TS.IFO");
        Make("VIDEO_TS/VIDEO_TS.BUP");
        Make("VIDEO_TS/VTS_01_0.IFO");
        Make("VIDEO_TS/VTS_01_0.BUP");
        Make("VIDEO_TS/VTS_01_1.VOB");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdVideo, v.Type);
        Assert.True(v.LooksWellFormed);
        Assert.Empty(v.Issues);
    }

    [Fact]
    public void DvdVideoMissingBup_TypeDvdVideo_FlaggedAsMalformed()
    {
        Make("VIDEO_TS/VIDEO_TS.IFO");
        Make("VIDEO_TS/VTS_01_1.VOB");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdVideo, v.Type);
        Assert.False(v.LooksWellFormed);
        Assert.Contains(v.Issues, i => i.Contains("VIDEO_TS.BUP"));
    }

    [Fact]
    public void DvdVideoMissingVobs_FlaggedNoVideo()
    {
        Make("VIDEO_TS/VIDEO_TS.IFO");
        Make("VIDEO_TS/VIDEO_TS.BUP");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdVideo, v.Type);
        Assert.False(v.LooksWellFormed);
        Assert.Contains(v.Issues, i => i.Contains("VTS_*.VOB"));
    }

    [Fact]
    public void DvdAudioComplete_TypeDvdAudio_WellFormed()
    {
        Make("AUDIO_TS/AUDIO_TS.IFO");
        Make("AUDIO_TS/AUDIO_TS.BUP");
        Make("AUDIO_TS/ATS_01_0.IFO");
        Make("AUDIO_TS/ATS_01_1.AOB");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdAudio, v.Type);
        Assert.True(v.LooksWellFormed);
    }

    [Fact]
    public void DvdAudioVideoHybrid_TypeHybrid()
    {
        Make("VIDEO_TS/VIDEO_TS.IFO");
        Make("VIDEO_TS/VIDEO_TS.BUP");
        Make("VIDEO_TS/VTS_01_1.VOB");
        Make("AUDIO_TS/AUDIO_TS.IFO");
        Make("AUDIO_TS/AUDIO_TS.BUP");
        Make("AUDIO_TS/ATS_01_1.AOB");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdAudioVideoHybrid, v.Type);
        Assert.True(v.LooksWellFormed);
    }

    [Fact]
    public void DvdVideoWithEmptyAudioTs_StillTypeDvdVideo()
    {
        // Pure DVD-Video discs are SPEC-REQUIRED to have an empty AUDIO_TS\
        // folder. Earlier the validator misclassified this as DvdAudioVideoHybrid.
        Make("VIDEO_TS/VIDEO_TS.IFO");
        Make("VIDEO_TS/VIDEO_TS.BUP");
        Make("VIDEO_TS/VTS_01_1.VOB");
        Directory.CreateDirectory(Path.Combine(_tempDir, "AUDIO_TS"));
        // ... but no files inside.

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.DvdVideo, v.Type);
        Assert.True(v.LooksWellFormed);
        Assert.Contains(v.Findings, f => f.Contains("AUDIO_TS\\ folder present"));
    }

    [Fact]
    public void Vcd_TypeVideoCd_WellFormed_HasModeNote()
    {
        Make("VCD/INFO.VCD");
        Make("VCD/ENTRIES.VCD");
        Make("MPEGAV/AVSEQ01.DAT");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.VideoCd, v.Type);
        Assert.True(v.LooksWellFormed);
        // Sanity: the Mode-2-caveat note shows up as a finding.
        Assert.Contains(v.Findings, f => f.Contains("Mode 2"));
    }

    [Fact]
    public void Svcd_TypeSuperVideoCd()
    {
        Make("SVCD/INFO.SVD");
        Make("SVCD/ENTRIES.SVD");
        Make("MPEGAV/AVSEQ01.DAT");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.SuperVideoCd, v.Type);
        Assert.True(v.LooksWellFormed);
    }

    [Fact]
    public void BluRay_TypeBluRayMovie()
    {
        Make("BDMV/index.bdmv");
        Make("BDMV/MovieObject.bdmv");
        Make("BDMV/STREAM/00001.m2ts");

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.BluRayMovie, v.Type);
        Assert.True(v.LooksWellFormed);
    }

    [Fact]
    public void VcdMissingMpegav_FlaggedAsMissingVideo()
    {
        Make("VCD/INFO.VCD");
        Make("VCD/ENTRIES.VCD");
        // No MPEGAV folder

        var v = DiscFolderValidator.Validate(_tempDir);
        Assert.Equal(DiscFolderValidator.DiscType.VideoCd, v.Type);
        Assert.False(v.LooksWellFormed);
        Assert.Contains(v.Issues, i => i.Contains("MPEGAV"));
    }
}
