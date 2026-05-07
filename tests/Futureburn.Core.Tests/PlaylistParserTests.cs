using Futureburn.Core.Audio;

namespace Futureburn.Core.Tests;

public class PlaylistParserTests : IDisposable
{
    private readonly string _tempDir;

    public PlaylistParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"futureburn-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WritePlaylist(string content)
    {
        var path = Path.Combine(_tempDir, "test.m3u8");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_SimpleM3U_ReturnsTracksWithoutTitlesOrDurations()
    {
        var path = WritePlaylist(
            "song1.mp3\n" +
            "song2.mp3\n" +
            "song3.mp3\n");

        var pl = PlaylistParser.Load(path);
        Assert.False(pl.IsExtended);
        Assert.Equal(3, pl.Entries.Count);
        Assert.All(pl.Entries, e =>
        {
            Assert.Null(e.Title);
            Assert.Null(e.Duration);
        });
        Assert.Equal("song1.mp3", pl.Entries[0].OriginalPath);
    }

    [Fact]
    public void Load_ExtendedM3U_ParsesEXTINFTitleAndDuration()
    {
        var path = WritePlaylist(
            "#EXTM3U\n" +
            "#EXTINF:200,Artist - Title\n" +
            "track1.wav\n" +
            "#EXTINF:90,Other Artist - Other Title\n" +
            "track2.wav\n");

        var pl = PlaylistParser.Load(path);
        Assert.True(pl.IsExtended);
        Assert.Equal(2, pl.Entries.Count);

        Assert.Equal("Artist - Title", pl.Entries[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(200), pl.Entries[0].Duration);

        Assert.Equal("Other Artist - Other Title", pl.Entries[1].Title);
        Assert.Equal(TimeSpan.FromSeconds(90), pl.Entries[1].Duration);
    }

    [Fact]
    public void Load_TitleWithCommas_KeepsTrailingPart()
    {
        // EXTINF must split on FIRST comma; titles can contain more.
        var path = WritePlaylist(
            "#EXTM3U\n" +
            "#EXTINF:120,Artist, Featured - Title, with, commas\n" +
            "track.wav\n");

        var pl = PlaylistParser.Load(path);
        Assert.Equal("Artist, Featured - Title, with, commas", pl.Entries[0].Title);
    }

    [Fact]
    public void Load_BlankLinesAndUnknownComments_AreIgnored()
    {
        var path = WritePlaylist(
            "#EXTM3U\n" +
            "\n" +
            "# this is just a comment\n" +
            "#PLAYLIST: my mix\n" +
            "song.mp3\n" +
            "\n");

        var pl = PlaylistParser.Load(path);
        Assert.Single(pl.Entries);
        Assert.Equal("song.mp3", pl.Entries[0].OriginalPath);
    }

    [Fact]
    public void Load_RelativePaths_ResolveAgainstPlaylistDirectory()
    {
        var path = WritePlaylist("a/b.mp3\n");
        var pl = PlaylistParser.Load(path);
        Assert.True(Path.IsPathRooted(pl.Entries[0].Path));
        Assert.EndsWith(Path.Combine("a", "b.mp3"), pl.Entries[0].Path);
        Assert.Equal("a/b.mp3", pl.Entries[0].OriginalPath);
    }

    [Fact]
    public void Load_AbsolutePath_KeptAsIs()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\music\song.mp3" : "/music/song.mp3";
        var path = WritePlaylist($"{abs}\n");
        var pl = PlaylistParser.Load(path);
        Assert.Equal(abs, pl.Entries[0].Path);
    }

    [Fact]
    public void TotalDuration_SumsExtinfDurations()
    {
        var path = WritePlaylist(
            "#EXTM3U\n" +
            "#EXTINF:100,a\n" +
            "a.mp3\n" +
            "#EXTINF:200,b\n" +
            "b.mp3\n");

        var pl = PlaylistParser.Load(path);
        Assert.Equal(TimeSpan.FromSeconds(300), pl.TotalDuration);
    }

    [Fact]
    public void Load_NegativeOrZeroDuration_LeavesNull()
    {
        // -1 in EXTINF means "unknown duration" — the spec convention.
        var path = WritePlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1,Streaming track\n" +
            "stream.mp3\n");

        var pl = PlaylistParser.Load(path);
        Assert.Null(pl.Entries[0].Duration);
        Assert.Equal("Streaming track", pl.Entries[0].Title);
    }
}
