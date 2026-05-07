using Futureburn.Core.Net;

namespace Futureburn.Core.Tests;

public class MusicBrainzTests
{
    [Fact]
    public void ComputeDiscId_IsDeterministic()
    {
        // Same TOC always produces the same disc ID.
        var lbas = new[] { 0, 14976, 49770, 63281, 73297 };
        var leadOut = 281457;
        var id1 = MusicBrainz.ComputeDiscId(lbas, leadOut);
        var id2 = MusicBrainz.ComputeDiscId(lbas, leadOut);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeDiscId_ProducesUrlSafeBase64()
    {
        var lbas = new[] { 0, 14976, 49770, 63281, 73297 };
        var leadOut = 281457;
        var id = MusicBrainz.ComputeDiscId(lbas, leadOut);

        // MusicBrainz uses + → ., / → _, = → -. The result should contain
        // none of the original base64-non-alphanumeric chars.
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('=', id);
        // SHA-1 is 20 bytes → ceil(20/3)*4 = 28 base64 chars (with padding).
        Assert.Equal(28, id.Length);
    }

    [Fact]
    public void ComputeDiscId_DifferentTocs_DifferentIds()
    {
        var idA = MusicBrainz.ComputeDiscId(new[] { 0, 100, 200 }, 1000);
        var idB = MusicBrainz.ComputeDiscId(new[] { 0, 100, 201 }, 1000);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public void ComputeDiscId_RejectsZeroTracks()
    {
        Assert.Throws<ArgumentException>(() =>
            MusicBrainz.ComputeDiscId(Array.Empty<int>(), 100));
    }

    [Fact]
    public void ComputeDiscId_Rejects100Tracks()
    {
        var lbas = Enumerable.Range(0, 100).Select(i => i * 100).ToArray();
        Assert.Throws<ArgumentException>(() =>
            MusicBrainz.ComputeDiscId(lbas, 100000));
    }

    [Fact]
    public void ParseResponse_SingleRelease_ExtractsArtistTitleTracks()
    {
        // Trimmed example matching the real MB API's shape.
        var json = """
        {
          "id": "fakeDISCID",
          "releases": [
            {
              "id": "release-uuid-here",
              "title": "Some Album",
              "date": "1998",
              "artist-credit": [{ "name": "Some Artist" }],
              "media": [
                {
                  "tracks": [
                    { "number": "1", "title": "Track One",   "length": 60000 },
                    { "number": "2", "title": "Track Two",   "length": 90500 }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var result = MusicBrainz.ParseResponse("fakeDISCID", json);
        Assert.True(result.Found);
        Assert.Single(result.Releases);
        var rel = result.Releases[0];
        Assert.Equal("Some Album", rel.Title);
        Assert.Equal("Some Artist", rel.Artist);
        Assert.Equal("1998", rel.Date);
        Assert.Equal(2, rel.Tracks.Count);
        Assert.Equal("Track One", rel.Tracks[0].Title);
        Assert.Equal(TimeSpan.FromMilliseconds(60000), rel.Tracks[0].Duration);
        Assert.Equal("Track Two", rel.Tracks[1].Title);
        Assert.Equal(TimeSpan.FromMilliseconds(90500), rel.Tracks[1].Duration);
    }

    [Fact]
    public void ParseResponse_MultipleArtistsWithJoinPhrase_ConcatenatesCorrectly()
    {
        var json = """
        {
          "id": "x",
          "releases": [
            {
              "id": "y",
              "title": "Collab",
              "artist-credit": [
                { "name": "Artist A", "joinphrase": " feat. " },
                { "name": "Artist B" }
              ],
              "media": []
            }
          ]
        }
        """;
        var result = MusicBrainz.ParseResponse("x", json);
        Assert.Equal("Artist A feat. Artist B", result.Releases[0].Artist);
    }

    [Fact]
    public void ParseResponse_NoReleases_ReturnsEmpty()
    {
        var json = """{"id": "abc", "releases": []}""";
        var result = MusicBrainz.ParseResponse("abc", json);
        Assert.False(result.Found);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public void ParseResponse_TrackNumberAsNumberOrString_BothWork()
    {
        // MB sometimes emits track numbers as JSON strings, sometimes as numbers.
        var json = """
        {
          "id": "x",
          "releases": [
            {
              "id": "y", "title": "T", "artist-credit": [{ "name": "A" }],
              "media": [{ "tracks": [
                { "number": 5, "title": "as number" },
                { "number": "7", "title": "as string" }
              ]}]
            }
          ]
        }
        """;
        var result = MusicBrainz.ParseResponse("x", json);
        Assert.Equal(5, result.Releases[0].Tracks[0].Number);
        Assert.Equal(7, result.Releases[0].Tracks[1].Number);
    }
}
