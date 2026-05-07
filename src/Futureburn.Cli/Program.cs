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
    "burn"                         => BurnCommand(args),
    "imapi-v1-info"                => ImapiV1Info(),
    "spti-info"                    => SptiInfo(args),
    "cd-info"                      => CdInfo(args),
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
    Console.WriteLine("    flags: --dry-run     plan only, no actual burn");
    Console.WriteLine("           --speed Nx    set burn speed (v2 only; default = max supported)");
    Console.WriteLine("           --force       overwrite a non-blank disc (CD-RW only)");
    Console.WriteLine("           --yes / -y    skip the y/N confirmation prompt");
    Console.WriteLine("           --keep-temp   keep decoded WAVs in the temp dir after we finish");
    Console.WriteLine("           --engine v2|v1   pick the IMAPI engine (default v2; v1 for legacy drives)");
    Console.WriteLine();
    Console.WriteLine("  futureburn imapi-v1-info              Diagnose whether IMAPI v1 works here");
    Console.WriteLine("  futureburn spti-info <drive>          SCSI INQUIRY via SPTI (proves the SPTI path works)");
    Console.WriteLine("  futureburn cd-info <drive>            Read the disc's TOC: track listing, types, durations");
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
    int? speedSps    = ParseSpeedFlag(args);
    string engine    = (FlagValue(args, "--engine") ?? "v2").ToLowerInvariant();
    if (engine is not ("v1" or "v2"))
    {
        Console.Error.WriteLine($"Unknown engine '{engine}'. Use v2 (default) or v1.");
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
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"cd-info failed: {ex.Message}");
        return 1;
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
