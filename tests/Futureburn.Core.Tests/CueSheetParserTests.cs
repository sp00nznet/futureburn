using Futureburn.Core.Image;

namespace Futureburn.Core.Tests;

public class CueSheetParserTests : IDisposable
{
    private readonly string _tempDir;

    public CueSheetParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cue-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteCue(string content, string binFileName = "image.bin")
    {
        // Create a stub BIN file so any FILE-existence checks elsewhere are happy.
        File.WriteAllBytes(Path.Combine(_tempDir, binFileName), new byte[2048]);
        var cuePath = Path.Combine(_tempDir, "image.cue");
        File.WriteAllText(cuePath, content);
        return cuePath;
    }

    [Fact]
    public void Parse_MinimalSingleTrack_Mode1_2048()
    {
        var path = WriteCue(
            "FILE \"image.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2048\n" +
            "    INDEX 01 00:00:00\n");

        var cue = CueSheetParser.Parse(path);
        Assert.Equal("BINARY", cue.BinFormat);
        Assert.Equal(Path.Combine(_tempDir, "image.bin"), cue.BinFile);
        Assert.Single(cue.Tracks);
        var t = cue.Tracks[0];
        Assert.Equal(1, t.Number);
        Assert.Equal(CueTrackMode.Mode1, t.Mode);
        Assert.Equal(2048, t.SectorBytes);
        Assert.Equal(0, t.IndexOneLba);
        Assert.True(cue.IsSingleDataTrack);
    }

    [Fact]
    public void Parse_Mode1_2352_RecognizedAsRaw()
    {
        var path = WriteCue(
            "FILE \"image.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n" +
            "    INDEX 01 00:00:00\n");

        var cue = CueSheetParser.Parse(path);
        Assert.Equal(2352, cue.Tracks[0].SectorBytes);
        Assert.Equal(CueTrackMode.Mode1, cue.Tracks[0].Mode);
    }

    [Fact]
    public void Parse_AudioTracks_RecognizedAsAudio()
    {
        var path = WriteCue(
            "FILE \"image.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 00 03:30:00\n" +
            "    INDEX 01 03:32:00\n");

        var cue = CueSheetParser.Parse(path);
        Assert.Equal(2, cue.Tracks.Count);
        Assert.True(cue.IsAllAudio);
        Assert.Equal(CueTrackMode.Audio, cue.Tracks[0].Mode);
        Assert.Equal(2352, cue.Tracks[0].SectorBytes);
    }

    [Fact]
    public void Parse_MsfConvertsToLba()
    {
        var path = WriteCue(
            "FILE \"image.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 00 03:30:00\n" +    // (3*60 + 30) * 75 = 15750
            "    INDEX 01 03:32:00\n");    // (3*60 + 32) * 75 = 15900

        var cue = CueSheetParser.Parse(path);
        Assert.Equal(15750, cue.Tracks[1].IndexZeroLba);
        Assert.Equal(15900, cue.Tracks[1].IndexOneLba);
    }

    [Fact]
    public void Parse_RemAndMetadataLines_AreIgnored()
    {
        var path = WriteCue(
            "REM GENRE Hip-Hop\n" +
            "REM DATE 1998\n" +
            "PERFORMER \"OutKast\"\n" +
            "TITLE \"Aquemini\"\n" +
            "FILE \"image.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2048\n" +
            "    INDEX 01 00:00:00\n");

        var cue = CueSheetParser.Parse(path);
        Assert.Single(cue.Tracks);
    }

    [Fact]
    public void Parse_QuotedFilenameWithSpaces()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "my image.bin"), new byte[2048]);
        var cuePath = Path.Combine(_tempDir, "spaces.cue");
        File.WriteAllText(cuePath,
            "FILE \"my image.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2048\n" +
            "    INDEX 01 00:00:00\n");

        var cue = CueSheetParser.Parse(cuePath);
        Assert.EndsWith("my image.bin", cue.BinFile);
    }

    [Fact]
    public void Parse_RelativeBinPath_ResolvesAgainstCueDir()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_tempDir, "sub")).FullName;
        File.WriteAllBytes(Path.Combine(sub, "image.bin"), new byte[2048]);
        var cuePath = Path.Combine(_tempDir, "rel.cue");
        File.WriteAllText(cuePath,
            "FILE \"sub/image.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2048\n" +
            "    INDEX 01 00:00:00\n");

        var cue = CueSheetParser.Parse(cuePath);
        Assert.True(Path.IsPathRooted(cue.BinFile));
        Assert.True(File.Exists(cue.BinFile));
    }

    [Fact]
    public void Parse_NoFile_Throws()
    {
        var path = Path.Combine(_tempDir, "bad.cue");
        File.WriteAllText(path, "  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n");
        Assert.Throws<InvalidDataException>(() => CueSheetParser.Parse(path));
    }
}
