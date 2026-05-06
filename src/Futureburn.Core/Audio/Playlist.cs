using System.Text;

namespace Futureburn.Core.Audio;

public sealed record PlaylistEntry(
    // Resolved absolute path. May not exist on disk (caller checks).
    string Path,
    // The path as written in the playlist file (could be relative).
    string OriginalPath,
    // Title from #EXTINF, if the playlist is extended.
    string? Title,
    // Duration from #EXTINF, if present.
    TimeSpan? Duration);

public sealed record Playlist(
    string SourcePath,
    bool IsExtended,
    IReadOnlyList<PlaylistEntry> Entries)
{
    public TimeSpan TotalDuration =>
        TimeSpan.FromSeconds(Entries.Where(e => e.Duration.HasValue)
                                     .Sum(e => e.Duration!.Value.TotalSeconds));
}

// Parses M3U / M3U8 playlists. Supports both:
//   simple   — one path per line, '#' lines are comments
//   extended — '#EXTM3U' header, '#EXTINF:<seconds>,<title>' before each track
//
// Encoding: M3U is technically Windows-1252, M3U8 is UTF-8. We always read as
// UTF-8 since that's what most modern playlists use, and ASCII-only paths
// (overwhelmingly common) decode identically either way.
public static class PlaylistParser
{
    public static Playlist Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var baseDir  = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var lines    = File.ReadAllLines(fullPath, Encoding.UTF8);

        var entries  = new List<PlaylistEntry>();
        bool isExtended = false;
        string?  pendingTitle    = null;
        TimeSpan? pendingDuration = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                isExtended = true;
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                ParseExtInf(line, out pendingDuration, out pendingTitle);
                continue;
            }
            // Any other '#' line is a comment or an extension we don't care about.
            if (line[0] == '#') continue;

            var resolved = Path.IsPathRooted(line)
                ? line
                : Path.GetFullPath(Path.Combine(baseDir, line));

            entries.Add(new PlaylistEntry(resolved, line, pendingTitle, pendingDuration));
            pendingTitle    = null;
            pendingDuration = null;
        }

        return new Playlist(fullPath, isExtended, entries);
    }

    private static void ParseExtInf(string line, out TimeSpan? duration, out string? title)
    {
        duration = null;
        title    = null;

        // Format: #EXTINF:<seconds>,<title>
        // The title can contain commas, so split on the FIRST comma only.
        var colon = line.IndexOf(':');
        if (colon < 0) return;

        var rest    = line[(colon + 1)..];
        var comma   = rest.IndexOf(',');
        var durStr  = comma < 0 ? rest : rest[..comma];

        // Duration is a signed integer, possibly -1 for unknown.
        if (int.TryParse(durStr.Trim(), out int seconds) && seconds > 0)
            duration = TimeSpan.FromSeconds(seconds);

        if (comma >= 0)
        {
            var t = rest[(comma + 1)..].Trim();
            if (t.Length > 0) title = t;
        }
    }
}
