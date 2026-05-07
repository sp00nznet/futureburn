using System.Globalization;

namespace Futureburn.Core.Image;

// Minimal text-mode CUE sheet parser. We only support the subset that
// matters for burning: FILE, TRACK, INDEX. PERFORMER / TITLE / FLAGS /
// PREGAP / POSTGAP / REM are silently ignored — they're metadata, not
// burn-relevant.
//
// Single-FILE cue sheets only (the common case). Multi-file cue sheets
// (one .bin per track) get a clear error.
//
// MSF in CUE is M:S:F where F = frames (75 per second). LBA = (M*60+S)*75 + F.

public static class CueSheetParser
{
    public static CueSheet Parse(string cuePath)
    {
        if (!File.Exists(cuePath))
            throw new FileNotFoundException($"Cue sheet not found: {cuePath}", cuePath);

        var lines    = File.ReadAllLines(cuePath);
        var dir      = Path.GetDirectoryName(Path.GetFullPath(cuePath))
                       ?? Directory.GetCurrentDirectory();

        string? binFile   = null;
        string  binFormat = "BINARY";
        var     tracks    = new List<CueTrack>();

        int? currentTrackNum = null;
        CueTrackMode currentMode = CueTrackMode.Unknown;
        int currentSectorBytes = 0;
        long? currentIndex0 = null;
        long? currentIndex1 = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("REM", StringComparison.OrdinalIgnoreCase)) continue;

            var keyword = NextToken(ref line).ToUpperInvariant();
            switch (keyword)
            {
                case "FILE":
                    if (binFile != null)
                        throw new NotSupportedException(
                            "Multi-FILE cue sheets aren't supported yet (one .bin per cue is the supported shape).");
                    binFile = TakeQuotedOrToken(ref line);
                    var fmtTok = NextToken(ref line);
                    if (fmtTok.Length > 0) binFormat = fmtTok.ToUpperInvariant();
                    if (!Path.IsPathRooted(binFile))
                        binFile = Path.GetFullPath(Path.Combine(dir, binFile));
                    break;

                case "TRACK":
                    // Flush previous track if any.
                    if (currentTrackNum is { } prevNum)
                        tracks.Add(MakeTrack(prevNum, currentMode, currentSectorBytes,
                                             currentIndex0, currentIndex1));
                    currentTrackNum    = ParseInt(NextToken(ref line));
                    var modeStr        = NextToken(ref line).ToUpperInvariant();
                    (currentMode, currentSectorBytes) = ParseMode(modeStr);
                    currentIndex0 = null;
                    currentIndex1 = null;
                    break;

                case "INDEX":
                    var indexNumStr = NextToken(ref line);
                    var msfStr      = NextToken(ref line);
                    int indexNum    = ParseInt(indexNumStr);
                    long lba        = MsfToLba(msfStr);
                    if      (indexNum == 0) currentIndex0 = lba;
                    else if (indexNum == 1) currentIndex1 = lba;
                    // higher INDEX values (sub-indices) we just ignore for burning
                    break;

                // Quietly accept these without doing anything special.
                case "PERFORMER":
                case "TITLE":
                case "FLAGS":
                case "ISRC":
                case "CATALOG":
                case "PREGAP":
                case "POSTGAP":
                case "SONGWRITER":
                case "CDTEXTFILE":
                    break;

                // Unknown keyword: be tolerant.
                default:
                    break;
            }
        }

        // Flush trailing track.
        if (currentTrackNum is { } finalNum)
            tracks.Add(MakeTrack(finalNum, currentMode, currentSectorBytes,
                                 currentIndex0, currentIndex1));

        if (binFile is null)
            throw new InvalidDataException("Cue sheet has no FILE directive.");
        if (tracks.Count == 0)
            throw new InvalidDataException("Cue sheet has no TRACK entries.");

        return new CueSheet(
            SourcePath: Path.GetFullPath(cuePath),
            BinFile:    binFile,
            BinFormat:  binFormat,
            Tracks:     tracks);
    }

    private static CueTrack MakeTrack(int num, CueTrackMode mode, int sectorBytes,
                                       long? idx0, long? idx1)
    {
        // INDEX 01 is required by the spec; default to 0 if missing (some
        // cue sheets omit it for the very first track of a single-track BIN).
        long indexOne = idx1 ?? 0;
        long indexZero = idx0 ?? indexOne;
        return new CueTrack(num, mode, sectorBytes, indexZero, indexOne);
    }

    private static (CueTrackMode mode, int sectorBytes) ParseMode(string modeStr) => modeStr switch
    {
        "AUDIO"           => (CueTrackMode.Audio, 2352),
        "MODE1/2048"      => (CueTrackMode.Mode1, 2048),
        "MODE1/2352"      => (CueTrackMode.Mode1, 2352),
        "MODE2/2336"      => (CueTrackMode.Mode2, 2336),
        "MODE2/2352"      => (CueTrackMode.Mode2, 2352),
        _                 => (CueTrackMode.Unknown, 0),
    };

    private static long MsfToLba(string msf)
    {
        // Format: MM:SS:FF (each two digits)
        var parts = msf.Split(':');
        if (parts.Length != 3) throw new FormatException($"Bad MSF: {msf}");
        int m = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int s = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int f = int.Parse(parts[2], CultureInfo.InvariantCulture);
        return (m * 60 + s) * 75 + f;
    }

    private static int ParseInt(string s) =>
        int.Parse(s, CultureInfo.InvariantCulture);

    // Pop the next whitespace-separated token from `line` (in place).
    private static string NextToken(ref string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        var tok = line.Substring(start, i - start);
        line = i < line.Length ? line.Substring(i) : "";
        return tok;
    }

    // Same as NextToken but if the next non-whitespace char is a quote,
    // consume up to the matching quote.
    private static string TakeQuotedOrToken(ref string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length) { line = ""; return ""; }
        if (line[i] == '"')
        {
            int start = i + 1;
            int end = line.IndexOf('"', start);
            if (end < 0) end = line.Length;
            var s = line.Substring(start, end - start);
            line = end + 1 < line.Length ? line.Substring(end + 1) : "";
            return s;
        }
        return NextToken(ref line);
    }
}
