using System.Reflection;
using Futureburn.Core.Audio;
using Futureburn.Core.Imapi;

string version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";

Console.WriteLine($"futureburn v{version}");

if (args.Length == 0)
{
    return PrintUsage();
}

return args[0].ToLowerInvariant() switch
{
    "drives" or "--drives" or "-d" => ListDrives(verbose: HasFlag(args, "-v") || HasFlag(args, "--verbose")),
    "disc"                         => InspectDisc(args.Length >= 2 ? args[1] : null),
    "probe"                        => ProbeAudio(args),
    "decode"                       => DecodeAudio(args),
    "playlist"                     => ShowPlaylist(args),
    "help" or "--help" or "-h"     => PrintUsage(),
    _                              => Unknown(args[0]),
};

static bool HasFlag(string[] args, string flag)
    => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static int PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("usage:");
    Console.WriteLine("  futureburn drives [-v|--verbose]   List optical drives + capabilities");
    Console.WriteLine("  futureburn disc <drive>            Inspect the disc loaded in a drive");
    Console.WriteLine("  futureburn probe <audio>           Show format / duration of an audio file");
    Console.WriteLine("  futureburn decode <in> <out.wav>   Decode any audio file to a CD-format WAV");
    Console.WriteLine("  futureburn playlist <file.m3u>     Parse and list an M3U / M3U8 playlist");
    Console.WriteLine();
    Console.WriteLine("audio formats: " + string.Join(", ", AudioDecoder.SupportedExtensions));
    Console.WriteLine();
    Console.WriteLine("Burning still isn't wired up. Coming in v0.0.6.");
    return 0;
}

static int ListDrives(bool verbose)
{
    var drives = DriveEnumerator.Enumerate();
    Console.WriteLine();

    if (drives.Count == 0)
    {
        Console.WriteLine("No optical drives found.");
        return 0;
    }

    Console.WriteLine($"Found {drives.Count} optical drive{(drives.Count == 1 ? "" : "s")}:");
    Console.WriteLine();

    foreach (var d in drives)
    {
        var letters = d.MountPoints.Count > 0 ? string.Join(", ", d.MountPoints) : "(no drive letter)";
        Console.WriteLine($"  {letters}  {d.VendorId} {d.ProductId} ({d.Revision})");

        var reads = FormatProfileList(d.ReadOnlyProfiles);
        var writes = FormatProfileList(d.WritableProfiles);
        if (!string.IsNullOrEmpty(reads))  Console.WriteLine($"    Reads:  {reads}");
        if (!string.IsNullOrEmpty(writes)) Console.WriteLine($"    Writes: {writes}");

        var loaded = d.CurrentProfiles.Where(p => p.Code != 0).Select(p => p.Name).ToList();
        Console.WriteLine($"    Loaded: {(loaded.Count > 0 ? string.Join(", ", loaded) : "(no disc)")}");

        if (verbose)
        {
            Console.WriteLine($"    Id:     {d.UniqueId}");
            Console.WriteLine($"    Can load media: {d.CanLoadMedia}");
            Console.WriteLine($"    All supported profiles ({d.SupportedProfiles.Count}):");
            foreach (var p in d.SupportedProfiles.OrderBy(p => p.Code))
                Console.WriteLine($"      {p.HexCode}  {p.Name}{(p.Writable ? "  [writable]" : "")}");
            Console.WriteLine($"    Feature pages ({d.SupportedFeaturePages.Count}):");
            var currentSet = d.CurrentFeaturePages.Select(f => f.Code).ToHashSet();
            foreach (var f in d.SupportedFeaturePages.OrderBy(f => f.Code))
                Console.WriteLine($"      {f.HexCode}  {f.Name}{(currentSet.Contains(f.Code) ? "  [active]" : "")}");
        }
        Console.WriteLine();
    }
    return 0;
}

static int InspectDisc(string? identifier)
{
    if (string.IsNullOrWhiteSpace(identifier))
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn disc <drive>");
        Console.WriteLine("  e.g. futureburn disc F:");
        return 1;
    }

    var drive = DriveEnumerator.Find(identifier);
    if (drive is null)
    {
        Console.WriteLine();
        Console.WriteLine($"Couldn't find a drive matching '{identifier}'.");
        Console.WriteLine("Try `futureburn drives` to see what's available.");
        return 1;
    }

    var letters = drive.MountPoints.Count > 0 ? string.Join(", ", drive.MountPoints) : drive.UniqueId;
    Console.WriteLine();
    Console.WriteLine($"Drive {letters} — {drive.VendorId} {drive.ProductId} ({drive.Revision})");
    Console.WriteLine();

    LoadedDisc disc;
    try
    {
        disc = DiscInspector.InspectDrive(drive);
    }
    catch (DiscInspector.NoMediaException ex)
    {
        Console.WriteLine($"  {ex.Message}");
        return 0;
    }

    Console.WriteLine($"  Media:    {disc.MediaTypeName}");

    if (!disc.HasFormatDetails)
    {
        Console.WriteLine();
        Console.WriteLine("  Format details unavailable. The disc may be finalized,");
        Console.WriteLine("  read-only (DVD-ROM / BD-ROM), or a non-data format (audio CD, etc.).");
        Console.WriteLine("  We'll dig deeper into these in later milestones.");
        return 0;
    }

    Console.WriteLine($"  Status:   {(disc.IsBlank ? "Blank" : "Has data")}");
    Console.WriteLine($"  Total:    {FormatBytes(disc.TotalBytes)} ({disc.TotalSectors:N0} sectors)");
    Console.WriteLine($"  Free:     {FormatBytes(disc.FreeBytes)} ({disc.FreeSectors:N0} sectors)");
    Console.WriteLine($"  Next LBA: {disc.NextWritableAddress:N0}");
    if (disc.CurrentWriteSpeedKbps > 0)
        Console.WriteLine($"  Current write speed: {disc.CurrentWriteSpeedKbps:N0} KB/s");
    if (disc.SupportedWriteSpeedsKbps.Count > 0)
        Console.WriteLine($"  Supported write speeds: {string.Join(", ", disc.SupportedWriteSpeedsKbps.Select(s => $"{s:N0} KB/s"))}");
    return 0;
}

static int ProbeAudio(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn probe <audio-file>");
        return 1;
    }
    var path = args[1];
    try
    {
        var info = AudioDecoder.Probe(path);
        Console.WriteLine();
        Console.WriteLine($"  File:     {info.Path}");
        Console.WriteLine($"  Format:   {info.SampleRate:N0} Hz, {info.Channels} ch, {info.BitsPerSample}-bit, {info.Encoding}");
        Console.WriteLine($"  Duration: {info.Duration:mm\\:ss\\.ff}");
        var minutes = info.EstimatedCdSectors / 75.0 / 60.0;
        Console.WriteLine($"  CD time:  {info.EstimatedCdSectors:N0} sectors ({minutes:0.00} min)");
        Console.WriteLine($"  CD-ready: {(info.IsCdFormat ? "yes — no resampling needed" : "no — will resample to 44.1 kHz / 16-bit / stereo")}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"probe failed: {ex.Message}");
        return 1;
    }
}

static int DecodeAudio(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn decode <input-audio> <output.wav>");
        return 1;
    }
    var input = args[1];
    var output = args[2];
    try
    {
        Console.WriteLine();
        Console.WriteLine($"Decoding {input}");
        Console.WriteLine($"      -> {output}");
        AudioDecoder.DecodeToCdWav(input, output);
        var fi = new FileInfo(output);
        Console.WriteLine($"Wrote {fi.Length:N0} bytes ({FormatBytes(fi.Length)}).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"decode failed: {ex.Message}");
        return 1;
    }
}

static int ShowPlaylist(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn playlist <file.m3u | file.m3u8>");
        return 1;
    }
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Playlist not found: {path}");
        return 1;
    }

    try
    {
        var pl = PlaylistParser.Load(path);
        Console.WriteLine();
        Console.WriteLine($"Loaded {pl.Entries.Count} track{(pl.Entries.Count == 1 ? "" : "s")} from {(pl.IsExtended ? "extended" : "simple")} M3U");
        Console.WriteLine($"  Source: {pl.SourcePath}");
        Console.WriteLine();

        int idx = 1;
        foreach (var e in pl.Entries)
        {
            var marker = File.Exists(e.Path) ? " " : "?";
            var title  = e.Title ?? Path.GetFileName(e.Path);
            var dur    = e.Duration is { } d ? $"  ({d:mm\\:ss})" : "";
            Console.WriteLine($"  {marker} {idx,2}. {title}{dur}");
            Console.WriteLine($"        {e.Path}");
            idx++;
        }

        if (pl.IsExtended && pl.TotalDuration > TimeSpan.Zero)
        {
            Console.WriteLine();
            var total = pl.TotalDuration;
            Console.WriteLine($"  Total: {total:hh\\:mm\\:ss}  (audio CD limit: 74-80 min)");
        }

        var missing = pl.Entries.Count(e => !File.Exists(e.Path));
        if (missing > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  ! {missing} of {pl.Entries.Count} tracks not found on disk (marked with '?').");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"playlist load failed: {ex.Message}");
        return 1;
    }
}

static int Unknown(string cmd)
{
    Console.WriteLine();
    Console.WriteLine($"Unknown command: {cmd}");
    Console.WriteLine("Try `futureburn help`.");
    return 1;
}

static string FormatBytes(long bytes)
{
    const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
    return bytes switch
    {
        < KB => $"{bytes} B",
        < MB => $"{bytes / (double)KB:0.##} KB",
        < GB => $"{bytes / (double)MB:0.##} MB",
        _    => $"{bytes / (double)GB:0.##} GB",
    };
}

static string FormatProfileList(IEnumerable<Mmc.ProfileInfo> profiles)
{
    var byCategory = profiles
        .GroupBy(p => p.Category)
        .OrderBy(g => g.Key)
        .Select(g => string.Join(", ", g.Select(p => p.Name).Distinct()));
    return string.Join("; ", byCategory);
}
