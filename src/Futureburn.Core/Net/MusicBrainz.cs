using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Futureburn.Core.Net;

// MusicBrainz disc-ID computation + lookup against the public web service.
//
// Disc ID algorithm (https://musicbrainz.org/doc/Disc_ID_Calculation):
//   1. Build an ASCII string of:
//        - first track number  (2 hex chars uppercase)
//        - last  track number  (2 hex chars uppercase)
//        - lead-out address    (8 hex chars uppercase; LBA + 150 frame offset)
//        - then 99 entries of 8 hex chars each: each track's start LBA + 150,
//          or "00000000" for tracks that don't exist.
//   2. SHA-1 the string.
//   3. Base64 encode the 20 hash bytes, with these substitutions:
//        +  →  .
//        /  →  _
//        =  →  -
//
// Lookup endpoint:
//   GET https://musicbrainz.org/ws/2/discid/{discid}?inc=artists+recordings&fmt=json
//   Required: User-Agent header (their public API blocks anonymous requests).
//
// Response: 200 with releases / 404 if the disc isn't in MB.

public static class MusicBrainz
{
    /// <summary>
    /// Compute a MusicBrainz disc ID from the disc's TOC.
    /// </summary>
    /// <param name="trackStartLbas">LBA where each track begins, in track order (1..N).</param>
    /// <param name="leadOutLba">LBA where the lead-out begins (= total disc length).</param>
    public static string ComputeDiscId(IReadOnlyList<int> trackStartLbas, int leadOutLba)
    {
        if (trackStartLbas.Count == 0 || trackStartLbas.Count > 99)
            throw new ArgumentException("Need 1-99 tracks");

        var sb = new StringBuilder(2 + 2 + 8 + 99 * 8);
        sb.Append("01");                                     // first track is always 1
        sb.Append(trackStartLbas.Count.ToString("X2"));      // last track number
        sb.Append((leadOutLba + 150).ToString("X8"));        // lead-out, +150 absolute frame offset

        for (int i = 0; i < 99; i++)
        {
            int offset = i < trackStartLbas.Count ? trackStartLbas[i] + 150 : 0;
            sb.Append(offset.ToString("X8"));
        }

        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
        var b64 = Convert.ToBase64String(hash);
        // MusicBrainz's URL-safe variant: + → ., / → _, = → -
        return b64.Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    public sealed record MbTrack(int Number, string Title, TimeSpan? Duration);
    public sealed record MbRelease(string Id, string Title, string Artist, string? Date, IReadOnlyList<MbTrack> Tracks);
    public sealed record MbLookupResult(string DiscId, IReadOnlyList<MbRelease> Releases)
    {
        public bool Found => Releases.Count > 0;
    }

    /// <summary>
    /// Query the public MusicBrainz API for releases matching this disc ID.
    /// Returns a lookup result with zero or more releases. Throws on transport
    /// errors; returns an empty result for 404 (disc not in the database).
    /// </summary>
    public static async Task<MbLookupResult> LookupAsync(
        string discId,
        string userAgent,
        CancellationToken ct = default)
    {
        var url = $"https://musicbrainz.org/ws/2/discid/{discId}?inc=artists+recordings&fmt=json";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // MB requires a polite User-Agent identifying the app + contact / repo.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new MbLookupResult(discId, Array.Empty<MbRelease>());
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResponse(discId, json);
    }

    /// <summary>
    /// Fuzzy MusicBrainz lookup by raw TOC, for discs whose exact disc ID isn't
    /// in the database (very common — a burned disc's TOC rarely reproduces a
    /// pressed CD's exactly). Uses the <c>discid/-?toc=</c> endpoint, which
    /// exact-matches the TOC if it can and otherwise returns releases whose
    /// track layout is close.
    /// </summary>
    /// <param name="trackStartLbas">LBA where each track begins, track order 1..N.</param>
    /// <param name="leadOutLba">LBA where the lead-out begins.</param>
    public static async Task<MbLookupResult> LookupByTocAsync(
        IReadOnlyList<int> trackStartLbas,
        int leadOutLba,
        string userAgent,
        CancellationToken ct = default)
    {
        if (trackStartLbas.Count == 0 || trackStartLbas.Count > 99)
            throw new ArgumentException("Need 1-99 tracks");

        // TOC parameter: firstTrack lastTrack leadOut(+150) track1(+150) ... trackN(+150).
        var toc = new StringBuilder();
        toc.Append("1 ").Append(trackStartLbas.Count).Append(' ').Append(leadOutLba + 150);
        foreach (var lba in trackStartLbas)
            toc.Append(' ').Append(lba + 150);

        var url = "https://musicbrainz.org/ws/2/discid/-"
                + $"?toc={Uri.EscapeDataString(toc.ToString())}"
                + "&inc=artists+recordings&fmt=json";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new MbLookupResult("(fuzzy)", Array.Empty<MbRelease>());
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResponse("(fuzzy)", json);
    }

    public static MbLookupResult ParseResponse(string discId, string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var releases = new List<MbRelease>();
        if (root.TryGetProperty("releases", out var relsArr))
        {
            foreach (var rel in relsArr.EnumerateArray())
            {
                var id     = rel.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var title  = rel.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                var date   = rel.TryGetProperty("date", out var dEl) ? dEl.GetString() : null;
                var artist = ExtractArtist(rel);

                var tracks = new List<MbTrack>();
                if (rel.TryGetProperty("media", out var mediaArr))
                {
                    foreach (var medium in mediaArr.EnumerateArray())
                    {
                        if (!medium.TryGetProperty("tracks", out var tracksArr)) continue;
                        foreach (var t in tracksArr.EnumerateArray())
                        {
                            int num = 0;
                            if (t.TryGetProperty("number", out var nEl))
                            {
                                if (nEl.ValueKind == JsonValueKind.String && int.TryParse(nEl.GetString(), out var ni)) num = ni;
                                else if (nEl.ValueKind == JsonValueKind.Number) num = nEl.GetInt32();
                            }
                            var trackTitle = t.TryGetProperty("title", out var ttEl) ? ttEl.GetString() ?? "" : "";
                            TimeSpan? duration = null;
                            if (t.TryGetProperty("length", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number)
                            {
                                duration = TimeSpan.FromMilliseconds(lenEl.GetInt64());
                            }
                            tracks.Add(new MbTrack(num, trackTitle, duration));
                        }
                    }
                }

                releases.Add(new MbRelease(id, title, artist, date, tracks));
            }
        }

        return new MbLookupResult(discId, releases);
    }

    private static string ExtractArtist(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var ac)) return "";
        var parts = new List<string>();
        foreach (var entry in ac.EnumerateArray())
        {
            if (entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                parts.Add(nameEl.GetString() ?? "");
            if (entry.TryGetProperty("joinphrase", out var jpEl) && jpEl.ValueKind == JsonValueKind.String)
            {
                var jp = jpEl.GetString();
                if (!string.IsNullOrEmpty(jp)) parts.Add(jp);
            }
        }
        return string.Concat(parts).Trim();
    }
}
