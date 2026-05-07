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
    "mkplaylist"                   => MakePlaylist(args),
    "burn"                         => BurnCommand(args),
    "burn-iso"                     => BurnIsoCommand(args),
    "mkiso"                        => MkIsoCommand(args),
    "burn-folder"                  => BurnFolderCommand(args),
    "imapi-v1-info"                => ImapiV1Info(),
    "spti-info"                    => SptiInfo(args),
    "cd-info"                      => CdInfo(args),
    "cd-lookup"                    => CdLookup(args),
    "ffmpeg"                       => FfmpegInfo(),
    "validate-folder"              => ValidateFolder(args),
    "vcd-author"                   => VcdAuthorCommand(args),
    "dvdv-author"                  => DvdVideoAuthorCommand(args),
    "finalize"                     => FinalizeDisc(args),
    "eject"                        => EjectDrive(args),
    "load"                         => LoadDrive(args),
    "help" or "--help" or "-h"     => PrintUsage(),
    _                              => Unknown(args[0]),
};

static bool HasFlag(string[] args, string flag)
    => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string? FlagValue(string[] args, string flag)
{
    for (int i = 0; i < args.Length; i++)
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1];
    return null;
}

static int PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("usage:");
    Console.WriteLine("  futureburn drives [-v|--verbose]      List optical drives + capabilities");
    Console.WriteLine("  futureburn disc <drive>               Inspect the disc loaded in a drive");
    Console.WriteLine("  futureburn probe <audio>              Show format / duration of an audio file");
    Console.WriteLine("  futureburn decode <in> <out.wav>      Decode any audio file to a CD-format WAV");
    Console.WriteLine("  futureburn playlist <file.m3u>        Parse and list an M3U / M3U8 playlist");
    Console.WriteLine("  futureburn burn <playlist> <drive>    Burn an audio CD from a playlist");
    Console.WriteLine("  futureburn burn-iso <iso> <drive>     Burn an ISO image to a blank CD-R or DVD-R");
    Console.WriteLine("    flags: --dry-run, --yes, --speed Nx");
    Console.WriteLine("  futureburn mkiso <folder> <out.iso>   Build an ISO 9660+Joliet+UDF image from a folder");
    Console.WriteLine("    flags: --label NAME, --fs all|iso|joliet|udf");
    Console.WriteLine("  futureburn burn-folder <folder> <drive>   mkiso + burn-iso, in one step (uses temp file)");
    Console.WriteLine("    flags: --label NAME, --fs ..., --speed Nx, --dry-run, --yes, --keep-iso");
    Console.WriteLine("    flags: --dry-run     plan only, no actual burn");
    Console.WriteLine("           --speed Nx    set burn speed (v2 only; default = max supported)");
    Console.WriteLine("           --force       overwrite a non-blank disc (CD-RW only)");
    Console.WriteLine("           --yes / -y    skip the y/N confirmation prompt");
    Console.WriteLine("           --keep-temp   keep decoded WAVs in the temp dir after we finish");
    Console.WriteLine("           --engine v2|v1|spti   pick the burn engine (default v2)");
    Console.WriteLine("           --gapless        DAO + cue-sheet burn for true gapless audio (spti only, experimental)");
    Console.WriteLine();
    Console.WriteLine("  futureburn imapi-v1-info              Diagnose whether IMAPI v1 works here");
    Console.WriteLine("  futureburn spti-info <drive>          SCSI INQUIRY via SPTI (proves the SPTI path works)");
    Console.WriteLine("  futureburn cd-info <drive>            Read the disc's TOC: track listing, types, durations");
    Console.WriteLine("  futureburn cd-lookup <drive>          Compute the disc ID and look it up on MusicBrainz");
    Console.WriteLine("  futureburn ffmpeg                     Detect ffmpeg (foundation for video disc authoring)");
    Console.WriteLine("  futureburn validate-folder <folder>   Recognize DVD-Video / DVD-Audio / VCD / SVCD / BD folder structures");
    Console.WriteLine("  futureburn vcd-author <input> <out>   Author a Video CD folder from a video file (experimental)");
    Console.WriteLine("    flags: --pal (default NTSC), --label NAME, --profile 1|2|3");
    Console.WriteLine("  futureburn dvdv-author <input> <out>  Author a DVD-Video folder from a video file (experimental)");
    Console.WriteLine("    flags: --pal (default NTSC), --label NAME");
    Console.WriteLine("  futureburn finalize <drive>           CLOSE SESSION on a disc with open tracks (salvage operation)");
    Console.WriteLine("  futureburn eject <drive>              Eject the drive tray");
    Console.WriteLine("  futureburn load <drive>               Close (load) the drive tray");
    Console.WriteLine();
    Console.WriteLine("audio formats: " + string.Join(", ", AudioDecoder.SupportedExtensions));
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
        return 1;
    }

    var drive = DriveEnumerator.Find(identifier);
    if (drive is null)
    {
        Console.WriteLine();
        Console.WriteLine($"Couldn't find a drive matching '{identifier}'.");
        return 1;
    }

    var letters = drive.MountPoints.Count > 0 ? string.Join(", ", drive.MountPoints) : drive.UniqueId;
    Console.WriteLine();
    Console.WriteLine($"Drive {letters} — {drive.VendorId} {drive.ProductId} ({drive.Revision})");
    Console.WriteLine();

    LoadedDisc disc;
    try { disc = DiscInspector.InspectDrive(drive); }
    catch (DiscInspector.NoMediaException ex) { Console.WriteLine($"  {ex.Message}"); return 0; }

    Console.WriteLine($"  Media:    {disc.MediaTypeName}");

    if (!disc.HasFormatDetails)
    {
        Console.WriteLine();
        Console.WriteLine("  Format details unavailable. The disc may be finalized,");
        Console.WriteLine("  read-only (DVD-ROM / BD-ROM), or a non-data format (audio CD, etc.).");
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
    if (args.Length < 2) { Console.WriteLine("\nusage: futureburn probe <audio-file>"); return 1; }
    try
    {
        var info = AudioDecoder.Probe(args[1]);
        Console.WriteLine();
        Console.WriteLine($"  File:     {info.Path}");
        Console.WriteLine($"  Format:   {info.SampleRate:N0} Hz, {info.Channels} ch, {info.BitsPerSample}-bit, {info.Encoding}");
        Console.WriteLine($"  Duration: {info.Duration:mm\\:ss\\.ff}");
        Console.WriteLine($"  CD time:  {info.EstimatedCdSectors:N0} sectors ({info.EstimatedCdSectors / 75.0 / 60.0:0.00} min)");
        Console.WriteLine($"  CD-ready: {(info.IsCdFormat ? "yes — no resampling needed" : "no — will resample")}");

        // Richer info via ffprobe when ffmpeg is installed.
        try
        {
            var p = Futureburn.Core.Ffmpeg.FfprobeRunner.Probe(args[1]);
            Console.WriteLine();
            Console.WriteLine("  --- ffprobe ---");
            Console.WriteLine($"  Container:  {p.Format.FormatLongName} ({p.Format.FormatName})");
            if (p.Format.BitRate is { } br)
                Console.WriteLine($"  Bitrate:    {br / 1000:N0} kbps overall");
            if (p.Format.Size is { } sz)
                Console.WriteLine($"  File size:  {FormatBytes(sz)}");
            foreach (var s in p.Streams.Where(s => s.CodecType == "audio"))
            {
                Console.WriteLine($"  Stream {s.Index} (audio): {s.CodecLongName} ({s.CodecName})" +
                                  (s.BitRate is { } sb ? $", {sb / 1000:N0} kbps" : ""));
            }
            foreach (var tag in p.Format.Tags.OrderBy(t => t.Key))
                Console.WriteLine($"    tag: {tag.Key} = {tag.Value}");
        }
        catch (InvalidOperationException)
        {
            // ffprobe not installed — skip silently. The basic probe above is enough.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (ffprobe details unavailable: {ex.Message})");
        }
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"probe failed: {ex.Message}"); return 1; }
}

static int DecodeAudio(string[] args)
{
    if (args.Length < 3) { Console.WriteLine("\nusage: futureburn decode <input-audio> <output.wav>"); return 1; }
    try
    {
        Console.WriteLine();
        Console.WriteLine($"Decoding {args[1]}");
        Console.WriteLine($"      -> {args[2]}");
        AudioDecoder.DecodeToCdWav(args[1], args[2]);
        var fi = new FileInfo(args[2]);
        Console.WriteLine($"Wrote {fi.Length:N0} bytes ({FormatBytes(fi.Length)}).");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"decode failed: {ex.Message}"); return 1; }
}

static int ShowPlaylist(string[] args)
{
    if (args.Length < 2) { Console.WriteLine("\nusage: futureburn playlist <file.m3u | file.m3u8>"); return 1; }
    if (!File.Exists(args[1])) { Console.Error.WriteLine($"Playlist not found: {args[1]}"); return 1; }

    try
    {
        var pl = PlaylistParser.Load(args[1]);
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
            Console.WriteLine($"  Total: {pl.TotalDuration:hh\\:mm\\:ss}  (audio CD limit: 74-80 min)");
        }
        var missing = pl.Entries.Count(e => !File.Exists(e.Path));
        if (missing > 0)
            Console.WriteLine($"\n  ! {missing} of {pl.Entries.Count} tracks not found on disk (marked with '?').");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"playlist load failed: {ex.Message}"); return 1; }
}

static int BurnIsoCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn burn-iso <iso-file> <drive> [--dry-run] [--speed Nx] [--yes]");
        Console.WriteLine("  e.g. futureburn burn-iso ubuntu.iso F: --dry-run");
        Console.WriteLine("       futureburn burn-iso my-disc.iso F: --speed 8x --yes");
        return 1;
    }

    var isoPath = args[1];
    var driveId = args[2];
    bool dryRun      = HasFlag(args, "--dry-run");
    bool skipConfirm = HasFlag(args, "--yes") || HasFlag(args, "-y");
    int? cdSpeedX    = ParseSpeedFlag(args) is { } sps ? sps / 75 : null;

    var drive = DriveEnumerator.Find(driveId);
    if (drive is null)
    {
        Console.Error.WriteLine($"Drive not found: {driveId}");
        return 1;
    }

    Futureburn.Core.Spti.SptiDataBurner.DataBurnPlan plan;
    try
    {
        plan = Futureburn.Core.Spti.SptiDataBurner.Plan(drive, isoPath);
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine($"\nPLAN FAILED: {ex.Message}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("--- ISO BURN PLAN ---");
    Console.WriteLine($"  ISO:          {plan.ImagePath}");
    Console.WriteLine($"  Size:         {FormatBytes(plan.ImageBytes)} ({plan.ImageSectors:N0} sectors of 2048 B)");
    Console.WriteLine($"  Drive:        {drive.PrimaryMount}  {drive.VendorId} {drive.ProductId} ({drive.Revision})");
    var profileCode = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
    Console.WriteLine($"  Disc:         {Mmc.LookupProfile(profileCode).Name}");
    Console.WriteLine($"  Mode:         {(plan.IsDvd ? "DVD data (SAO + Mode 1)" : "CD data (TAO + Mode 1)")}");
    Console.WriteLine($"  Speed:        {(cdSpeedX is { } x ? x + "x" : "drive default")}");
    Console.WriteLine();

    if (dryRun)
    {
        Console.WriteLine("DRY RUN COMPLETE — no actual burn performed.");
        return 0;
    }

    if (!skipConfirm)
    {
        Console.Write($"This will write {FormatBytes(plan.ImageBytes)} to {drive.PrimaryMount}. Continue? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("Aborted.");
            return 0;
        }
    }

    Console.WriteLine();
    Console.WriteLine("Burning. Don't unplug the drive or close this window.");
    Console.WriteLine();

    int lastReportedPct = -25;
    try
    {
        Futureburn.Core.Spti.SptiDataBurner.ExecuteBurn(
            plan,
            requestedSpeedX: cdSpeedX,
            onLog: msg => Console.WriteLine(msg),
            onProgress: (written, total) =>
            {
                int pct = total > 0 ? (int)(written * 100 / total) : 0;
                if (pct >= lastReportedPct + 5 || written == total)
                {
                    Console.WriteLine($"  {pct,3}%  ({FormatBytes(written)} / {FormatBytes(total)})");
                    lastReportedPct = pct;
                }
            });
        Console.WriteLine();
        Console.WriteLine("BURN COMPLETE. Disc finalized.");
        return 0;
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"BURN FAILED: {ex.Message}");
        return 1;
    }
}

static int MkIsoCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn mkiso <source-folder> <output.iso> [--label NAME] [--fs all|iso|joliet|udf]");
        return 1;
    }
    var folder = args[1];
    var output = args[2];
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Source folder not found: {folder}");
        return 1;
    }

    var label = FlagValue(args, "--label") ?? Path.GetFileName(Path.GetFullPath(folder));
    var fsArg = (FlagValue(args, "--fs") ?? "all").ToLowerInvariant();
    var fileSystems = ParseFileSystemFlag(fsArg);
    if (fileSystems is null)
    {
        Console.Error.WriteLine($"Unknown --fs '{fsArg}'. Use one of: all | iso | joliet | udf");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"  Source:  {folder}");
    Console.WriteLine($"  Output:  {output}");
    Console.WriteLine($"  Volume:  {label}");
    Console.WriteLine($"  FS:      {fileSystems.Value}");
    Console.WriteLine();
    Console.WriteLine("Building image ...");

    int lastPct = -5;
    try
    {
        var result = Futureburn.Core.Fs.FsImageBuilder.Build(
            folder, output, label, fileSystems.Value,
            (copied, total) =>
            {
                int pct = total > 0 ? (int)(copied * 100 / total) : 0;
                if (pct >= lastPct + 5)
                {
                    Console.WriteLine($"  {pct,3}% ({FormatBytes(copied)} / {FormatBytes(total)})");
                    lastPct = pct;
                }
            });

        Console.WriteLine();
        Console.WriteLine($"Wrote {FormatBytes(result.TotalBytes)} ({result.BlockCount:N0} sectors of {result.BlockSize} B) to {output}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"mkiso failed: {ex.Message}");
        try { File.Delete(output); } catch { }
        return 1;
    }
}

static int BurnFolderCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn burn-folder <source-folder> <drive> [--label NAME] [--fs ...]");
        Console.WriteLine("                              [--speed Nx] [--dry-run] [--yes] [--keep-iso]");
        return 1;
    }

    var folder  = args[1];
    var driveId = args[2];
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Source folder not found: {folder}");
        return 1;
    }

    var drive = DriveEnumerator.Find(driveId);
    if (drive is null)
    {
        Console.Error.WriteLine($"Drive not found: {driveId}");
        return 1;
    }

    var label = FlagValue(args, "--label") ?? Path.GetFileName(Path.GetFullPath(folder));
    var fsArg = (FlagValue(args, "--fs") ?? "all").ToLowerInvariant();
    var fileSystems = ParseFileSystemFlag(fsArg);
    if (fileSystems is null)
    {
        Console.Error.WriteLine($"Unknown --fs '{fsArg}'. Use one of: all | iso | joliet | udf");
        return 1;
    }

    int? cdSpeedX    = ParseSpeedFlag(args) is { } sps ? sps / 75 : null;
    bool dryRun      = HasFlag(args, "--dry-run");
    bool skipConfirm = HasFlag(args, "--yes") || HasFlag(args, "-y");
    bool keepIso     = HasFlag(args, "--keep-iso");

    var tempIso = Path.Combine(Path.GetTempPath(), $"futureburn-build-{Guid.NewGuid():N}.iso");

    Console.WriteLine();
    Console.WriteLine($"  Source: {folder}");
    Console.WriteLine($"  Drive:  {drive.PrimaryMount}  {drive.VendorId} {drive.ProductId}");
    Console.WriteLine($"  Volume: {label}");
    Console.WriteLine($"  FS:     {fileSystems.Value}");
    Console.WriteLine($"  ISO:    {tempIso}");
    Console.WriteLine();

    try
    {
        // Step 1: build the ISO.
        Console.WriteLine("Building ISO ...");
        int lastPct = -5;
        var built = Futureburn.Core.Fs.FsImageBuilder.Build(
            folder, tempIso, label, fileSystems.Value,
            (copied, total) =>
            {
                int pct = total > 0 ? (int)(copied * 100 / total) : 0;
                if (pct >= lastPct + 10)
                {
                    Console.WriteLine($"  build {pct,3}% ({FormatBytes(copied)} / {FormatBytes(total)})");
                    lastPct = pct;
                }
            });
        Console.WriteLine($"  ISO built: {FormatBytes(built.TotalBytes)} ({built.BlockCount:N0} sectors)");

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("DRY RUN — ISO built but not burned.");
            if (keepIso) Console.WriteLine($"ISO kept at: {tempIso}");
            return 0;
        }

        // Step 2: burn it.
        var plan = Futureburn.Core.Spti.SptiDataBurner.Plan(drive, tempIso);
        Console.WriteLine();
        Console.WriteLine($"  Disc:  {Mmc.LookupProfile(drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0).Name}");
        Console.WriteLine($"  Mode:  {(plan.IsDvd ? "DVD data (SAO + Mode 1)" : "CD data (TAO + Mode 1)")}");
        Console.WriteLine($"  Speed: {(cdSpeedX is { } x ? x + "x" : "drive default")}");

        if (!skipConfirm)
        {
            Console.Write($"\nThis will write {FormatBytes(built.TotalBytes)} to {drive.PrimaryMount}. Continue? [y/N] ");
            var answer = Console.ReadLine();
            if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("Aborted.");
                return 0;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Burning. Don't unplug the drive or close this window.");
        Console.WriteLine();

        int lastBurnPct = -5;
        Futureburn.Core.Spti.SptiDataBurner.ExecuteBurn(
            plan,
            requestedSpeedX: cdSpeedX,
            onLog: msg => Console.WriteLine(msg),
            onProgress: (written, total) =>
            {
                int pct = total > 0 ? (int)(written * 100 / total) : 0;
                if (pct >= lastBurnPct + 5)
                {
                    Console.WriteLine($"  burn  {pct,3}% ({FormatBytes(written)} / {FormatBytes(total)})");
                    lastBurnPct = pct;
                }
            });
        Console.WriteLine();
        Console.WriteLine("BURN COMPLETE. Disc finalized.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        return 1;
    }
    finally
    {
        if (!keepIso)
        {
            try { File.Delete(tempIso); } catch { }
        }
        else
        {
            Console.WriteLine($"\n(ISO kept at: {tempIso})");
        }
    }
}

static Futureburn.Core.Fs.FsImageBuilder.FileSystem? ParseFileSystemFlag(string s) => s switch
{
    "all"    => Futureburn.Core.Fs.FsImageBuilder.FileSystem.All,
    "iso"    => Futureburn.Core.Fs.FsImageBuilder.FileSystem.Iso9660 | Futureburn.Core.Fs.FsImageBuilder.FileSystem.Joliet,
    "joliet" => Futureburn.Core.Fs.FsImageBuilder.FileSystem.Iso9660 | Futureburn.Core.Fs.FsImageBuilder.FileSystem.Joliet,
    "udf"    => Futureburn.Core.Fs.FsImageBuilder.FileSystem.Udf,
    _        => null,
};

static int MakePlaylist(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn mkplaylist <folder> [--output file.m3u8] [--probe]");
        return 1;
    }
    var folder = args[1];
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Folder not found: {folder}");
        return 1;
    }

    string? output = FlagValue(args, "--output") ?? FlagValue(args, "-o");
    bool probe = HasFlag(args, "--probe") || HasFlag(args, "-p");

    var supportedExt = AudioDecoder.SupportedExtensions
        .Select(e => e.ToLowerInvariant())
        .ToHashSet();

    var files = new DirectoryInfo(folder)
        .GetFiles()
        .Where(f => supportedExt.Contains(f.Extension.ToLowerInvariant()))
        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
    {
        Console.Error.WriteLine($"No supported audio files in {folder}");
        Console.Error.WriteLine($"  Supported extensions: {string.Join(", ", AudioDecoder.SupportedExtensions)}");
        return 1;
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("#EXTM3U");

    int probeFailures = 0;
    foreach (var f in files)
    {
        if (probe)
        {
            try
            {
                var info = AudioDecoder.Probe(f.FullName);
                int seconds = (int)info.Duration.TotalSeconds;
                var title = Path.GetFileNameWithoutExtension(f.Name);
                sb.AppendLine($"#EXTINF:{seconds},{title}");
            }
            catch
            {
                probeFailures++;
                sb.AppendLine($"#EXTINF:-1,{Path.GetFileNameWithoutExtension(f.Name)}");
            }
        }
        // Path is just the filename — relative paths resolve against the
        // playlist's directory, which is the folder we just scanned.
        sb.AppendLine(f.Name);
    }

    if (output is not null)
    {
        File.WriteAllText(output, sb.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote {files.Count} track{(files.Count == 1 ? "" : "s")} to {output}");
        if (probe && probeFailures > 0)
            Console.WriteLine($"  ({probeFailures} file{(probeFailures == 1 ? "" : "s")} couldn't be probed; their EXTINF duration is -1)");
    }
    else
    {
        Console.Write(sb.ToString());
    }
    return 0;
}

static int BurnCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn burn <playlist> <drive> [--dry-run] [--speed Nx] [--force] [--yes] [--keep-temp] [--engine v2|v1]");
        Console.WriteLine("  e.g. futureburn burn mix.m3u8 F: --dry-run");
        Console.WriteLine("       futureburn burn mix.m3u8 F: --speed 16x");
        Console.WriteLine("       futureburn burn mix.m3u8 F: --engine v1");
        return 1;
    }

    var playlistPath = args[1];
    var driveId      = args[2];
    bool dryRun      = HasFlag(args, "--dry-run");
    bool force       = HasFlag(args, "--force");
    bool skipConfirm = HasFlag(args, "--yes") || HasFlag(args, "-y");
    bool keepTemp    = HasFlag(args, "--keep-temp");
    bool gapless     = HasFlag(args, "--gapless");
    int? speedSps    = ParseSpeedFlag(args);
    string engine    = (FlagValue(args, "--engine") ?? "v2").ToLowerInvariant();
    if (engine is not ("v1" or "v2" or "spti"))
    {
        Console.Error.WriteLine($"Unknown engine '{engine}'. Use v2 (default), v1, or spti.");
        return 1;
    }

    if (!File.Exists(playlistPath)) { Console.Error.WriteLine($"Playlist not found: {playlistPath}"); return 1; }

    var drive = DriveEnumerator.Find(driveId);
    if (drive is null)
    {
        Console.Error.WriteLine($"Drive not found: {driveId}");
        Console.Error.WriteLine("Try `futureburn drives` to see what's available.");
        return 1;
    }

    Playlist playlist;
    try { playlist = PlaylistParser.Load(playlistPath); }
    catch (Exception ex) { Console.Error.WriteLine($"playlist load failed: {ex.Message}"); return 1; }
    if (playlist.Entries.Count == 0) { Console.Error.WriteLine("Playlist is empty."); return 1; }

    Console.WriteLine();
    Console.WriteLine($"  Playlist: {playlist.SourcePath}");
    Console.WriteLine($"  Tracks:   {playlist.Entries.Count}");
    Console.WriteLine($"  Drive:    {drive.PrimaryMount} {drive.VendorId} {drive.ProductId} ({drive.Revision})");
    Console.WriteLine($"  Engine:   IMAPI {engine}");
    Console.WriteLine($"  Mode:     {(dryRun ? "DRY RUN — no actual burn" : "REAL BURN")}");
    Console.WriteLine();

    var tempDir = Path.Combine(Path.GetTempPath(), $"futureburn-{Guid.NewGuid():N}");
    Console.WriteLine($"Planning burn (decoding non-CD-format tracks if any) ...");

    if (engine == "v1")
    {
        return BurnViaV1(drive, playlist, tempDir, dryRun, skipConfirm, keepTemp);
    }
    if (engine == "spti")
    {
        // For SPTI the --speed flag is "Nx" (audio CD 1x = 176 KB/s).
        // Re-derive the X value from the parsed sps (1x = 75 sps).
        int? cdSpeedX = speedSps is { } sps ? sps / 75 : null;
        return BurnViaSpti(drive, playlist, tempDir, dryRun, skipConfirm, keepTemp, cdSpeedX, gapless);
    }
    if (gapless && engine != "spti")
    {
        Console.Error.WriteLine("--gapless requires --engine spti (TAO modes always insert 2-second gaps).");
        return 1;
    }

    AudioCdBurner.BurnPlan plan;
    try
    {
        plan = AudioCdBurner.Plan(drive, playlist, tempDir, speedSps, allowNonBlank: force);
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"PLAN FAILED:");
        Console.Error.WriteLine($"  {ex.Message}");
        TryCleanup(tempDir, keep: false);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"PLAN FAILED (unexpected): {ex.Message}");
        TryCleanup(tempDir, keep: false);
        return 1;
    }

    int decodedCount = plan.Tracks.Count(t => t.RequiredDecode);

    Console.WriteLine();
    Console.WriteLine("--- BURN PLAN ---");
    Console.WriteLine($"  Disc:      {drive.PrimaryMount}  {(plan.DiscIsBlank ? "BLANK" : "HAS EXISTING TRACKS")}");
    Console.WriteLine($"  Capacity:  {plan.DiscFreeSectors:N0} of {plan.DiscTotalSectors:N0} sectors free " +
                      $"({plan.DiscFreeSectors / 75.0 / 60.0:0.00} min)");
    Console.WriteLine();
    Console.WriteLine($"  Tracks ({plan.Tracks.Count}):");
    foreach (var t in plan.Tracks)
    {
        var title = t.Title ?? Path.GetFileName(t.SourcePath);
        var marker = t.RequiredDecode ? "*" : " ";
        Console.WriteLine($"    {marker} {t.Index,2}. {title}  ({t.Duration:mm\\:ss})");
    }
    if (decodedCount > 0)
        Console.WriteLine($"    (* = decoded to CD format in {tempDir})");

    Console.WriteLine();
    Console.WriteLine($"  Total time:    {plan.TotalDuration:hh\\:mm\\:ss}  ({plan.TotalSectors:N0} sectors)");
    Console.WriteLine($"  Speed:         {AudioCdBurner.SpsToCdX(plan.ChosenSpeedSps)}x ({plan.ChosenSpeedSps:N0} sectors/sec)");
    if (plan.SupportedSpeedsSps.Count > 0)
        Console.WriteLine($"  Supported:     {string.Join(", ", plan.SupportedSpeedsSps.Select(s => $"{AudioCdBurner.SpsToCdX(s)}x"))}");
    if (plan.EstimatedBurnTime > TimeSpan.Zero)
        Console.WriteLine($"  Est. burn time: ~{plan.EstimatedBurnTime:mm\\:ss}  (write only; finalization adds ~30 sec)");
    Console.WriteLine();

    if (dryRun)
    {
        Console.WriteLine("DRY RUN COMPLETE — no actual burn performed.");
        if (decodedCount > 0)
        {
            if (keepTemp)
                Console.WriteLine($"Decoded WAVs left at: {tempDir}");
            else
                TryCleanup(tempDir, keep: false);
        }
        else
        {
            TryCleanup(tempDir, keep: false);  // empty dir, just remove
        }
        return 0;
    }

    if (!skipConfirm)
    {
        Console.Write($"This will write to {drive.PrimaryMount}. Continue? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("Aborted.");
            TryCleanup(tempDir, keep: false);
            return 0;
        }
    }

    Console.WriteLine();
    Console.WriteLine("Burning. Don't unplug the drive or close this window.");
    Console.WriteLine();

    try
    {
        AudioCdBurner.ExecuteBurn(plan, (current, total) =>
        {
            Console.WriteLine($"  -> Track {current}/{total} ...");
        });
        Console.WriteLine();
        Console.WriteLine("BURN COMPLETE. Disc finalized.");
        return 0;
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"BURN FAILED: {ex.Message}");
        return 1;
    }
    finally
    {
        TryCleanup(tempDir, keep: keepTemp);
    }
}

static int BurnViaSpti(OpticalDrive drive, Playlist playlist, string tempDir,
                       bool dryRun, bool skipConfirm, bool keepTemp, int? cdSpeedX, bool gapless)
{
    Futureburn.Core.Spti.SptiAudioCdBurner.SptiBurnPlan plan;
    try
    {
        plan = Futureburn.Core.Spti.SptiAudioCdBurner.Plan(drive, playlist, tempDir);
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"PLAN FAILED (spti):");
        Console.Error.WriteLine($"  {ex.Message}");
        TryCleanup(tempDir, keep: false);
        return 1;
    }

    int decodedCount = plan.Tracks.Count(t => t.RequiredDecode);

    Console.WriteLine();
    Console.WriteLine("--- BURN PLAN (SPTI) ---");
    Console.WriteLine($"  Drive: {drive.PrimaryMount}  {drive.VendorId} {drive.ProductId}");
    Console.WriteLine();
    Console.WriteLine($"  Tracks ({plan.Tracks.Count}):");
    foreach (var t in plan.Tracks)
    {
        var title = t.Title ?? Path.GetFileName(t.SourcePath);
        var marker = t.RequiredDecode ? "*" : " ";
        Console.WriteLine($"    {marker} {t.Index,2}. {title}  ({t.Duration:mm\\:ss})");
    }
    if (decodedCount > 0)
        Console.WriteLine($"    (* = decoded to CD format in {tempDir})");
    Console.WriteLine();
    var trackTime = TimeSpan.FromSeconds(plan.TotalSectors / 75.0);
    Console.WriteLine($"  Total time:    {trackTime:hh\\:mm\\:ss}  ({plan.TotalSectors:N0} sectors)");
    Console.WriteLine($"  Mode:          {(gapless ? "DAO/SAO with cue sheet — GAPLESS (experimental, untested on hardware)" : "TAO with standard 2-second gaps (Red Book audio)")}");
    Console.WriteLine($"  Speed:         {(cdSpeedX is { } x ? x + "x" : "drive default (recommend --speed 4x or 8x for old USB writers)")}");
    Console.WriteLine();

    if (dryRun)
    {
        Console.WriteLine("DRY RUN COMPLETE (spti) — no actual burn performed.");
        TryCleanup(tempDir, keep: keepTemp && decodedCount > 0);
        return 0;
    }

    if (!skipConfirm)
    {
        Console.Write($"This will write to {drive.PrimaryMount} via raw SCSI. Continue? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("Aborted.");
            TryCleanup(tempDir, keep: false);
            return 0;
        }
    }

    Console.WriteLine();
    Console.WriteLine("Burning via SPTI. Don't unplug the drive or close this window.");
    Console.WriteLine();

    try
    {
        int lastTrack = -1;
        Futureburn.Core.Spti.SptiAudioCdBurner.ExecuteBurn(
            plan,
            requestedCdSpeedX: cdSpeedX,
            gapless: gapless,
            onLog: msg => Console.WriteLine(msg),
            onTrackStart: (current, total) =>
                Console.WriteLine($"  -> Track {current}/{total} ..."),
            onProgress: (current, total, written, totalBytes) =>
            {
                if (current != lastTrack)
                {
                    lastTrack = current;
                    return;
                }
                int pct = totalBytes > 0 ? (int)(written * 100 / totalBytes) : 0;
                if (written == totalBytes || (pct % 25 == 0 && pct > 0))
                    Console.WriteLine($"     {pct}% ({FormatBytes(written)} / {FormatBytes(totalBytes)})");
            });
        Console.WriteLine();
        Console.WriteLine("BURN COMPLETE (SPTI). Disc finalized.");

        // Post-burn self-verification: read the disc back and confirm the TOC
        // matches what the plan called for.
        Console.WriteLine();
        Console.WriteLine("Verifying ...");
        try
        {
            var v = Futureburn.Core.Spti.SptiAudioCdBurner.Verify(plan);
            if (v.Passed)
            {
                Console.WriteLine($"  VERIFIED: {v.TrackCount} tracks, " +
                                  $"{v.DiscStatus}/{v.SessionState}, all durations match.");
            }
            else
            {
                Console.WriteLine($"  Verification FOUND {v.Mismatches.Count} issue(s):");
                foreach (var m in v.Mismatches)
                    Console.WriteLine($"    - {m}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (verify step couldn't read the disc: {ex.Message})");
        }
        return 0;
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"BURN FAILED (spti): {ex.Message}");
        return 1;
    }
    finally
    {
        TryCleanup(tempDir, keep: keepTemp);
    }
}

static int BurnViaV1(OpticalDrive drive, Playlist playlist, string tempDir,
                     bool dryRun, bool skipConfirm, bool keepTemp)
{
    AudioCdBurnerV1.V1BurnPlan plan;
    try
    {
        plan = AudioCdBurnerV1.Plan(drive, playlist, tempDir);
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"PLAN FAILED (v1):");
        Console.Error.WriteLine($"  {ex.Message}");
        TryCleanup(tempDir, keep: false);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"PLAN FAILED (v1, unexpected): {ex.Message}");
        TryCleanup(tempDir, keep: false);
        return 1;
    }

    int decodedCount = plan.Tracks.Count(t => t.RequiredDecode);

    Console.WriteLine();
    Console.WriteLine("--- BURN PLAN (IMAPI v1) ---");
    Console.WriteLine($"  Disc capacity: {plan.AvailableBlocks:N0} of {plan.TotalBlocks:N0} blocks available " +
                      $"({plan.AvailableBlocks / 75.0 / 60.0:0.00} min)");
    Console.WriteLine($"  Block size:    {plan.BlockSize} bytes (CD-DA = 2352)");
    Console.WriteLine();
    Console.WriteLine($"  Tracks ({plan.Tracks.Count}):");
    foreach (var t in plan.Tracks)
    {
        var title = t.Title ?? Path.GetFileName(t.SourcePath);
        var marker = t.RequiredDecode ? "*" : " ";
        Console.WriteLine($"    {marker} {t.Index,2}. {title}  ({t.Duration:mm\\:ss})");
    }
    if (decodedCount > 0)
        Console.WriteLine($"    (* = decoded to CD format in {tempDir})");

    Console.WriteLine();
    long totalBlocks = plan.Tracks.Sum(t => t.Sectors);
    var trackTime = TimeSpan.FromSeconds(totalBlocks / 75.0);
    Console.WriteLine($"  Total time:    {trackTime:hh\\:mm\\:ss}  ({totalBlocks:N0} blocks)");
    Console.WriteLine($"  Note: IMAPI v1 chooses the burn speed automatically; the --speed flag is ignored.");
    Console.WriteLine();

    if (dryRun)
    {
        Console.WriteLine("DRY RUN COMPLETE (v1) — no actual burn performed.");
        TryCleanup(tempDir, keep: keepTemp && decodedCount > 0);
        return 0;
    }

    if (!skipConfirm)
    {
        Console.Write($"This will write to {drive.PrimaryMount} via IMAPI v1. Continue? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("Aborted.");
            TryCleanup(tempDir, keep: false);
            return 0;
        }
    }

    Console.WriteLine();
    Console.WriteLine("Burning via IMAPI v1. Don't unplug the drive or close this window.");
    Console.WriteLine();

    try
    {
        AudioCdBurnerV1.ExecuteBurn(plan, (current, total) =>
            Console.WriteLine($"  -> Track {current}/{total} ..."));
        Console.WriteLine();
        Console.WriteLine("BURN COMPLETE (IMAPI v1). Disc finalized.");
        return 0;
    }
    catch (AudioCdBurner.BurnException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"BURN FAILED (v1): {ex.Message}");
        return 1;
    }
    finally
    {
        TryCleanup(tempDir, keep: keepTemp);
    }
}

static int SptiInfo(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn spti-info <drive>");
        Console.WriteLine("  e.g. futureburn spti-info F");
        Console.WriteLine();
        Console.WriteLine("Note: opening a drive for SCSI pass-through usually requires running");
        Console.WriteLine("an elevated (Administrator) PowerShell. SPTI is the path we'll use to");
        Console.WriteLine("write CDs without going through IMAPI at all.");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);
    Console.WriteLine();
    Console.WriteLine($"Opening {letter}:\\ for SCSI pass-through ...");
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        Console.WriteLine($"  Device path:  {dev.DevicePath}");
        var inq = dev.Inquiry();
        Console.WriteLine($"  Vendor:       {inq.Vendor}");
        Console.WriteLine($"  Product:      {inq.Product}");
        Console.WriteLine($"  Revision:     {inq.Revision}");
        Console.WriteLine();
        Console.WriteLine("SPTI works. The SPTI burn engine itself is scaffolded but not yet implemented;");
        Console.WriteLine("for actual burning, use --engine v2 (default) or --engine v1 (legacy fallback).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"SPTI failed: {ex.Message}");
        return 1;
    }
}

static int EjectDrive(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine("\nusage: futureburn eject <drive>");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        dev.EjectTray();
        Console.WriteLine($"\nEjected {letter}:\\");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"eject failed: {ex.Message}");
        return 1;
    }
}

static int LoadDrive(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine("\nusage: futureburn load <drive>");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        dev.LoadTray();
        Console.WriteLine($"\nLoaded (closed tray) {letter}:\\");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"load failed: {ex.Message}");
        return 1;
    }
}

static int DvdVideoAuthorCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn dvdv-author <input-video> <output-folder> [--pal] [--label NAME]");
        Console.WriteLine();
        Console.WriteLine("EXPERIMENTAL: produces a DVD-Video folder with VIDEO_TS\\ and AUDIO_TS\\,");
        Console.WriteLine("  ffmpeg-transcoded VOB files, and SKELETON IFO/BUP files.");
        Console.WriteLine();
        Console.WriteLine("  Will play in: VLC, MPC-HC, and other software players that");
        Console.WriteLine("                read VOBs without strict IFO parsing.");
        Console.WriteLine("  Probably WON'T play in: standalone DVD players, which read the");
        Console.WriteLine("                          IFO tables to navigate.");
        Console.WriteLine();
        Console.WriteLine("  For production-quality discs use DVDStyler, DVDFlick, or dvdauthor");
        Console.WriteLine("  to author the IFOs, then `futureburn burn-folder <result> <drive>`.");
        return 1;
    }

    var input     = args[1];
    var outFolder = args[2];
    bool isPal    = HasFlag(args, "--pal");
    var label     = FlagValue(args, "--label") ?? Path.GetFileNameWithoutExtension(input);

    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"Input video not found: {input}");
        return 1;
    }

    var ffmpeg = Futureburn.Core.Ffmpeg.FfmpegRunner.Locate();
    if (ffmpeg is null)
    {
        Console.Error.WriteLine("ffmpeg not found. Install with: winget install Gyan.FFmpeg");
        return 1;
    }

    var videoTs = Path.Combine(outFolder, "VIDEO_TS");
    var audioTs = Path.Combine(outFolder, "AUDIO_TS");
    Directory.CreateDirectory(videoTs);
    Directory.CreateDirectory(audioTs);   // required by spec, must exist even if empty

    var vobPath = Path.Combine(videoTs, "VTS_01_1.VOB");

    Console.WriteLine();
    Console.WriteLine($"  Input:    {input}");
    Console.WriteLine($"  Output:   {outFolder}");
    Console.WriteLine($"  System:   {(isPal ? "PAL" : "NTSC")}");
    Console.WriteLine($"  Label:    {label}");
    Console.WriteLine();
    Console.WriteLine($"Transcoding via ffmpeg ({ffmpeg.VersionLine}) ...");
    Console.WriteLine($"  Target:   {(isPal ? "pal-dvd" : "ntsc-dvd")} (MPEG-2 video + AC-3 audio in DVD-PS)");
    Console.WriteLine();

    // ffmpeg's -target presets: pal-dvd / ntsc-dvd. These set:
    //   video = mpeg2video, 720x480 NTSC or 720x576 PAL, ~6000 kbps target
    //   audio = ac3, 448 kbps stereo, 48 kHz
    //   muxer = dvd
    var ffargs = new[]
    {
        "-y", "-i", input,
        "-target", isPal ? "pal-dvd" : "ntsc-dvd",
        // Cap output at the DVD-Video per-VOB limit (1 GB - 1 sector). Beyond
        // this we'd need to split into VTS_01_2.VOB, _3.VOB, etc. Doable but
        // future work. Short content stays under this; full-length movies will
        // truncate and warrant a "split into multiple VOBs" enhancement.
        "-fs", "1073709056",
        vobPath,
    };

    var rr = ffmpeg.Run(ffargs, line =>
    {
        if (line.StartsWith("frame=") || line.Contains("Error") || line.StartsWith("[error]"))
            Console.WriteLine($"  {line}");
    });

    if (rr.ExitCode != 0 || !File.Exists(vobPath))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"ffmpeg failed (exit {rr.ExitCode}). Last lines of log:");
        var tail = rr.CombinedLog.Split('\n').TakeLast(8);
        foreach (var l in tail) Console.Error.WriteLine($"  {l}");
        return 1;
    }

    // Write skeleton IFO + BUP files. BUP is the spec-required exact backup
    // copy of the IFO — players verify them against each other.
    var vmg     = Futureburn.Core.Authoring.DvdIfoBuilder.BuildVmgIfo(numTitleSets: 1, providerId: label);
    var vtsIfo  = Futureburn.Core.Authoring.DvdIfoBuilder.BuildVtsIfo();
    File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"), vmg);
    File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.BUP"), vmg);
    File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.IFO"), vtsIfo);
    File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.BUP"), vtsIfo);

    var vobSize = new FileInfo(vobPath).Length;
    Console.WriteLine();
    Console.WriteLine("--- Authoring complete ---");
    Console.WriteLine($"  VTS_01_1.VOB:  {FormatBytes(vobSize)}");
    Console.WriteLine($"  VIDEO_TS.IFO:  2048 bytes  (skeleton — no PGC / no chapters)");
    Console.WriteLine($"  VIDEO_TS.BUP:  2048 bytes  (mirror of IFO)");
    Console.WriteLine($"  VTS_01_0.IFO:  2048 bytes  (skeleton)");
    Console.WriteLine($"  VTS_01_0.BUP:  2048 bytes  (mirror)");
    Console.WriteLine($"  AUDIO_TS\\      empty (required by spec)");
    Console.WriteLine();
    Console.WriteLine("To burn the resulting folder:");
    Console.WriteLine($"  futureburn burn-folder \"{outFolder}\" F: --label \"{label}\"");
    Console.WriteLine();
    Console.WriteLine("⚠ EXPERIMENTAL: skeleton IFOs only. The disc will play in VLC and other");
    Console.WriteLine("  software DVD-Video readers (they accept the VOBs directly). Standalone");
    Console.WriteLine("  hardware DVD players probably WON'T accept it — they navigate via the");
    Console.WriteLine("  IFO tables we don't write yet (TT_SRPT, PGCI, VOBU address map, etc.).");
    Console.WriteLine();
    Console.WriteLine("  For production-quality DVD-Video discs that play in any player, author");
    Console.WriteLine("  with a real tool (DVDStyler, DVDFlick, or command-line dvdauthor) and");
    Console.WriteLine("  then burn the resulting VIDEO_TS folder with `burn-folder`.");
    return 0;
}

static int VcdAuthorCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn vcd-author <input-video> <output-folder> [--pal] [--label NAME] [--profile 1|2|3]");
        Console.WriteLine();
        Console.WriteLine("EXPERIMENTAL: produces a VCD folder structure (VCD/INFO.VCD,");
        Console.WriteLine("  VCD/ENTRIES.VCD, MPEGAV/AVSEQ01.DAT). Plays in VLC and other");
        Console.WriteLine("  software players; strict-spec standalone VCD players may reject it");
        Console.WriteLine("  because we burn single-track data CDs (real VCDs are multi-track).");
        return 1;
    }
    var input     = args[1];
    var outFolder = args[2];
    bool isPal    = HasFlag(args, "--pal");
    var label     = FlagValue(args, "--label") ?? Path.GetFileNameWithoutExtension(input);
    int profile   = int.TryParse(FlagValue(args, "--profile"), out var p) && p is 1 or 2 or 3 ? p : 2;

    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"Input video not found: {input}");
        return 1;
    }

    var ffmpeg = Futureburn.Core.Ffmpeg.FfmpegRunner.Locate();
    if (ffmpeg is null)
    {
        Console.Error.WriteLine("ffmpeg not found. Install with: winget install Gyan.FFmpeg");
        Console.Error.WriteLine("Then run `futureburn ffmpeg` to verify before retrying.");
        return 1;
    }

    Directory.CreateDirectory(Path.Combine(outFolder, "VCD"));
    Directory.CreateDirectory(Path.Combine(outFolder, "MPEGAV"));

    var avseqPath = Path.Combine(outFolder, "MPEGAV", "AVSEQ01.DAT");

    Console.WriteLine();
    Console.WriteLine($"  Input:    {input}");
    Console.WriteLine($"  Output:   {outFolder}");
    Console.WriteLine($"  Profile:  VCD {(profile == 1 ? "1.0" : profile == 2 ? "1.1" : "2.0")}");
    Console.WriteLine($"  System:   {(isPal ? "PAL" : "NTSC")}");
    Console.WriteLine($"  Label:    {label}");
    Console.WriteLine();
    Console.WriteLine($"Transcoding via ffmpeg ({ffmpeg.VersionLine}) ...");
    Console.WriteLine($"  Target:   {(isPal ? "pal-vcd" : "ntsc-vcd")} (MPEG-1 video + MP2 audio in MPEG-PS)");

    // ffmpeg's -target presets: pal-vcd / ntsc-vcd. These set:
    //   video = mpeg1video, 352x288 PAL or 352x240 NTSC, ~1150 kbps
    //   audio = mp2, 224 kbps stereo, 44.1 kHz
    //   muxer = mpeg
    // Output extension should be .mpg or .dat — ffmpeg picks based on extension.
    var ffargs = new[]
    {
        "-y", "-i", input,
        "-target", isPal ? "pal-vcd" : "ntsc-vcd",
        avseqPath,
    };

    Console.WriteLine();
    var rr = ffmpeg.Run(ffargs, line =>
    {
        // Surface progress + errors; suppress most chatter.
        if (line.StartsWith("frame=") || line.Contains("Error") || line.StartsWith("[error]"))
            Console.WriteLine($"  {line}");
    });

    if (rr.ExitCode != 0 || !File.Exists(avseqPath))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"ffmpeg failed (exit {rr.ExitCode}). Last lines of log:");
        var tail = rr.CombinedLog.Split('\n').TakeLast(8);
        foreach (var l in tail) Console.Error.WriteLine($"  {l}");
        return 1;
    }

    // Write the VCD metadata files. INFO.VCD doesn't depend on disc layout
    // so we write it normally. ENTRIES.VCD nominally points at MMC tracks 2..N
    // by MSF position; for our single-track burn we put a placeholder track-2
    // entry at LBA 0. Strict players will likely refuse this; software players
    // are typically tolerant.
    var infoPath    = Path.Combine(outFolder, "VCD", "INFO.VCD");
    var entriesPath = Path.Combine(outFolder, "VCD", "ENTRIES.VCD");
    File.WriteAllBytes(infoPath,
        Futureburn.Core.Authoring.VcdInfoBuilder.Build(label, isPal, systemProfile: profile));
    File.WriteAllBytes(entriesPath,
        Futureburn.Core.Authoring.VcdEntriesBuilder.Build(new[]
        {
            new Futureburn.Core.Authoring.VcdEntriesBuilder.TrackEntry(MmcTrackNumber: 2, StartLba: 0),
        }));

    var avseqSize = new FileInfo(avseqPath).Length;
    Console.WriteLine();
    Console.WriteLine("--- Authoring complete ---");
    Console.WriteLine($"  AVSEQ01.DAT:  {FormatBytes(avseqSize)}");
    Console.WriteLine($"  INFO.VCD:     2048 bytes");
    Console.WriteLine($"  ENTRIES.VCD:  2048 bytes");
    Console.WriteLine();
    Console.WriteLine("To burn the resulting folder:");
    Console.WriteLine($"  futureburn burn-folder \"{outFolder}\" F: --label \"{label}\"");
    Console.WriteLine();
    Console.WriteLine("⚠ EXPERIMENTAL: This produces a single-track data CD with a VCD-shaped");
    Console.WriteLine("  file system. VLC, MPC-HC, and most modern software DVD/VCD players");
    Console.WriteLine("  will play it. Older standalone VCD players that strictly require");
    Console.WriteLine("  multi-track CDs may not. Multi-track CD-data writing is a separate");
    Console.WriteLine("  future project.");
    return 0;
}

static int FfmpegInfo()
{
    Console.WriteLine();
    Console.WriteLine("Looking for ffmpeg ...");
    var info = Futureburn.Core.Tools.FfmpegLocator.Locate();
    if (info is null)
    {
        Console.WriteLine();
        Console.WriteLine("  Not found.");
        Console.WriteLine();
        Console.WriteLine("  Install via one of:");
        Console.WriteLine("    winget install Gyan.FFmpeg");
        Console.WriteLine("    choco  install ffmpeg");
        Console.WriteLine("    scoop  install ffmpeg");
        Console.WriteLine("    https://www.gyan.dev/ffmpeg/builds/");
        Console.WriteLine();
        Console.WriteLine("  ffmpeg is the foundation for any future MKV → DVD-Video, MKV → VCD,");
        Console.WriteLine("  or hi-res audio → DVD-Audio authoring. We don't bundle it (licensing).");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"  Path:    {info.Path}");
    Console.WriteLine($"  Version: {info.VersionLine}");
    Console.WriteLine();
    Console.WriteLine("  ffmpeg is available. Video disc authoring (DVD-Video / VCD / SVCD)");
    Console.WriteLine("  will be able to use this when those subsystems land.");
    return 0;
}

static int ValidateFolder(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn validate-folder <folder>");
        return 1;
    }
    var folder = args[1];
    var v = Futureburn.Core.Fs.DiscFolderValidator.Validate(folder);

    Console.WriteLine();
    Console.WriteLine($"  Folder: {Path.GetFullPath(folder)}");
    Console.WriteLine($"  Type:   {v.Type}");
    Console.WriteLine($"  Status: {(v.LooksWellFormed ? "well-formed (should burn to a valid disc)" : "issues found — see below")}");

    if (v.Findings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Findings:");
        foreach (var f in v.Findings) Console.WriteLine($"    - {f}");
    }
    if (v.Issues.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Issues:");
        foreach (var i in v.Issues) Console.WriteLine($"    ! {i}");
    }
    return v.LooksWellFormed ? 0 : 1;
}

static int CdLookup(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine("\nusage: futureburn cd-lookup <drive>");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);

    string discId;
    Futureburn.Core.Spti.SptiDevice.DiscToc toc;
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        toc = dev.ReadToc();
        if (toc.Tracks.Count == 0)
        {
            Console.Error.WriteLine("Disc has no readable TOC.");
            return 1;
        }
        var startLbas = toc.Tracks.Select(t => t.StartLba).ToArray();
        discId = Futureburn.Core.Net.MusicBrainz.ComputeDiscId(startLbas, toc.LeadOutLba);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Couldn't read TOC: {ex.Message}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"  Disc ID:    {discId}");
    Console.WriteLine($"  MB lookup:  https://musicbrainz.org/ws/2/discid/{discId}?inc=artists+recordings");
    Console.WriteLine();
    Console.WriteLine("Querying MusicBrainz ...");

    Futureburn.Core.Net.MusicBrainz.MbLookupResult result;
    try
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        var ua = $"futureburn/{version} ( https://github.com/sp00nznet/futureburn )";
        result = Futureburn.Core.Net.MusicBrainz.LookupAsync(discId, ua).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Lookup failed: {ex.Message}");
        return 1;
    }

    if (!result.Found)
    {
        Console.WriteLine();
        Console.WriteLine("No matching release in MusicBrainz. The disc isn't in the database");
        Console.WriteLine("(or the TOC differs from any known pressing of it).");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"Found {result.Releases.Count} release{(result.Releases.Count == 1 ? "" : "s")}:");
    int relIdx = 1;
    foreach (var rel in result.Releases)
    {
        Console.WriteLine();
        Console.WriteLine($"  [{relIdx}] {rel.Artist}");
        Console.WriteLine($"      {rel.Title}{(rel.Date is null ? "" : "  (" + rel.Date + ")")}");
        Console.WriteLine($"      MB id: {rel.Id}");
        if (rel.Tracks.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("       #   Title                                                Duration");
            Console.WriteLine("      --   ---------------------------------------------------  --------");
            foreach (var t in rel.Tracks)
            {
                var title = t.Title.Length > 50 ? t.Title.Substring(0, 47) + "..." : t.Title;
                var dur   = t.Duration is { } d ? d.ToString(@"mm\:ss") : "  ?  ";
                Console.WriteLine($"      {t.Number,2}   {title,-50}  {dur}");
            }
        }
        relIdx++;
    }
    return 0;
}

static int FinalizeDisc(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn finalize <drive>");
        Console.WriteLine();
        Console.WriteLine("Issues SCSI CLOSE SESSION (opcode 0x5B function 2) to write the");
        Console.WriteLine("disc's lead-out and TOC. Use this to salvage a partially-burned");
        Console.WriteLine("disc that has at least one complete track but failed mid-burn.");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);
    Console.WriteLine();
    Console.WriteLine($"Finalizing {letter}:\\ via SCSI CLOSE SESSION ...");
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        var info = dev.ReadDiscInformation();
        Console.WriteLine($"  Before: Status = {info.Status}, LastSession = {info.LastSessionState}");

        // Close the session. function = 2 = close current session.
        // This blocks until the drive writes the lead-out (~30-60 sec typical).
        Console.WriteLine("  CLOSE SESSION ... (this can take a minute)");
        dev.CloseTrackOrSession(function: 2, trackNumber: 0, timeoutSec: 300);

        var after = dev.ReadDiscInformation();
        Console.WriteLine($"  After:  Status = {after.Status}, LastSession = {after.LastSessionState}");
        if (after.IsPlayablyFinalized)
            Console.WriteLine("  Disc is finalized. Should play in standalone players.");
        else
            Console.WriteLine("  Hmm — disc still doesn't report finalized. Try `cd-info <drive>` to look closer.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"finalize failed: {ex.Message}");
        return 1;
    }
}

static int CdInfo(string[] args)
{
    if (args.Length < 2 || args[1].Length < 1 || !char.IsLetter(args[1][0]))
    {
        Console.WriteLine();
        Console.WriteLine("usage: futureburn cd-info <drive>");
        Console.WriteLine("  e.g. futureburn cd-info F");
        return 1;
    }
    char letter = char.ToUpperInvariant(args[1][0]);
    Console.WriteLine();
    Console.WriteLine($"Reading TOC from {letter}:\\ ...");
    try
    {
        using var dev = Futureburn.Core.Spti.SptiDevice.OpenDriveLetter(letter);
        var inq = dev.Inquiry();
        Console.WriteLine($"  Drive: {inq.Vendor} {inq.Product} ({inq.Revision})");

        var info = dev.ReadDiscInformation();
        Console.WriteLine();
        Console.WriteLine($"  Disc Status:    {info.Status}{(info.IsPlayablyFinalized ? "  (will play in standalone players)" : "  (NOT fully finalized — players may refuse it)")}");
        Console.WriteLine($"  Last Session:   {info.LastSessionState}");
        Console.WriteLine($"  Sessions:       {info.Sessions}");
        Console.WriteLine($"  Erasable:       {(info.Erasable ? "yes" : "no")}");
        Console.WriteLine($"  Disc Type:      {info.DiscTypeName}  (raw 0x{info.DiscTypeCode:X2})");
        if (info.DiscIdValid)
            Console.WriteLine($"  Disc ID:        0x{info.DiscId:X8}");

        var toc = dev.ReadToc();
        Console.WriteLine();
        Console.WriteLine($"  Tracks {toc.FirstTrackNumber}-{toc.LastTrackNumber} " +
                          $"({toc.Tracks.Count} total), lead-out at LBA {toc.LeadOutLba:N0}");
        var typeLabel = toc.HasAudio && toc.HasData ? "Mixed-mode (audio + data)"
                       : toc.HasAudio                ? "Audio CD"
                                                     : "Data disc";
        Console.WriteLine($"  Disc layout:    {typeLabel}");
        Console.WriteLine($"  Total time:     {toc.TotalDuration:hh\\:mm\\:ss}");
        Console.WriteLine();
        Console.WriteLine("   #   Type             Start LBA       Length      Duration");
        Console.WriteLine("  --  ---------------  ------------  ------------  --------");
        foreach (var t in toc.Tracks)
        {
            Console.WriteLine($"  {t.Number,2}  {t.TypeLabel,-15}  {t.StartLba,12:N0}  {t.LengthLba,12:N0}  {t.Duration:mm\\:ss}");
        }

        // For data discs (or mixed-mode), also show what's on the file system.
        if (toc.HasData)
        {
            ShowFileSystem(letter);
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"cd-info failed: {ex.Message}");
        return 1;
    }
}

static void ShowFileSystem(char driveLetter)
{
    try
    {
        var root = new DirectoryInfo($"{driveLetter}:\\");
        if (!root.Exists) return;

        Console.WriteLine();
        Console.WriteLine($"--- File system on {driveLetter}:\\ ---");

        // Specific disc-type detection from well-known folder structure.
        // Defer to the shared validator so CLI + GUI agree on labels.
        var v = Futureburn.Core.Fs.DiscFolderValidator.Validate($"{driveLetter}:\\");
        var label = v.Type switch
        {
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.DvdVideo            => "DVD-Video",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.DvdAudio            => "DVD-Audio",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.DvdAudioVideoHybrid => "Hybrid DVD-Audio + DVD-Video",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.VideoCd             => "Video CD (VCD)",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.SuperVideoCd        => "Super Video CD (SVCD)",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.BluRayMovie         => "Blu-ray Movie",
            Futureburn.Core.Fs.DiscFolderValidator.DiscType.DataDisc            => "Data CD/DVD",
            _                                                                    => "Unknown",
        };
        Console.WriteLine($"  Disc type: {label}");

        var entries = root.GetFileSystemInfos();

        Console.WriteLine();
        Console.WriteLine("  Top-level entries:");
        foreach (var e in entries.OrderByDescending(e => (e.Attributes & FileAttributes.Directory) != 0)
                                  .ThenBy(e => e.Name))
        {
            bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
            if (isDir)
            {
                Console.WriteLine($"    [DIR]  {e.Name}/");
            }
            else
            {
                var fi = (FileInfo)e;
                Console.WriteLine($"           {e.Name}  ({FormatBytes(fi.Length)})");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (file system read failed: {ex.Message})");
    }
}

static int ImapiV1Info()
{
    Console.WriteLine();
    Console.WriteLine("Probing IMAPI v1 ...");
    var d = AudioCdBurnerV1.Diagnose();

    Console.WriteLine();
    Console.WriteLine($"  master.Open():        {(d.MasterOpened ? "OK" : "FAILED")}");
    Console.WriteLine($"  Recorders enumerated: {d.RecorderCount}");
    foreach (var p in d.RecorderPaths)
        Console.WriteLine($"     - {p}");
    Console.WriteLine($"  Redbook format:       {(d.RedbookFormatAvailable ? "AVAILABLE" : "unavailable")}");
    if (d.AudioBlockSize.HasValue)
        Console.WriteLine($"  Audio block size:     {d.AudioBlockSize.Value} bytes");
    if (d.TotalAudioBlocks.HasValue)
        Console.WriteLine($"  Total blocks:         {d.TotalAudioBlocks.Value:N0}");
    if (d.AvailableAudioBlocks.HasValue)
        Console.WriteLine($"  Available blocks:     {d.AvailableAudioBlocks.Value:N0}  ({d.AvailableAudioBlocks.Value / 75.0 / 60.0:0.00} min)");
    if (d.Error is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"  Error: {d.Error}");
    }
    return 0;
}

static int? ParseSpeedFlag(string[] args)
{
    var v = FlagValue(args, "--speed");
    if (v is null) return null;
    var s = v.Trim().ToLowerInvariant();
    // Accept "16x" or "16" — both mean 16x audio CD speed (= 1200 sectors/sec).
    if (s.EndsWith("x")) s = s[..^1];
    if (int.TryParse(s, out int n) && n > 0)
        return AudioCdBurner.CdXToSps(n);
    return null;
}

static void TryCleanup(string tempDir, bool keep)
{
    if (keep) return;
    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
    catch { /* best-effort */ }
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
