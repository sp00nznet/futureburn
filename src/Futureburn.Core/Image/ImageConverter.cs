using System.Runtime.Versioning;

namespace Futureburn.Core.Image;

// Convert a disc image to a plain ISO (2048-byte-per-sector user data) —
// Daemon Tools' "convert images", Tier B. Supported inputs:
//   .iso              already ISO — straight copy
//   .cue (+ .bin)     CDRWIN / generic BIN-CUE (reuses BinCueImageStream)
//   .bin              raw BIN with no cue — sector layout auto-detected
//   .mdf (+ .mds)     Alcohol 120% / DAEMON Tools image
//   .nrg              Nero image (v1 "NERO" / v2 "NER5" footer)
//
// The MDF and (data-track) NRG bodies are just CD/DVD sectors, so rather than
// fully decode every header we detect the sector layout from the CD-ROM Mode-1
// sync mark and pull the 2048-byte payload out of each sector. NRG's trailing
// chunk section is located via its footer so we don't copy metadata into the
// ISO. Single-data-track images only (the ISO-convertible case).

[SupportedOSPlatform("windows")]
public static class ImageConverter
{
    public const int IsoSector = 2048;

    // The 12-byte sync mark every raw (2352/2336-with-sync) CD data sector opens
    // with: 00 FF*10 00. Its presence means the body is raw, not cooked 2048.
    private static readonly byte[] SyncMark =
        { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    public sealed record ConvertResult(string SourceFormat, long Sectors, int SourceSectorSize, long IsoBytes);

    public static ConvertResult ToIso(string input, string outputIso,
                                      Action<long, long>? onProgress = null,
                                      Action<string>? onLog = null)
    {
        if (!File.Exists(input)) throw new FileNotFoundException($"Image not found: {input}");
        var ext = Path.GetExtension(input).ToLowerInvariant();
        return ext switch
        {
            ".iso"          => CopyThrough(input, outputIso, onProgress, onLog),
            ".cue"          => FromBinCue(input, outputIso, onProgress, onLog),
            ".bin"          => FromRawBody(input, 0, new FileInfo(input).Length, "BIN", outputIso, onProgress, onLog),
            ".mds" or ".mdf"=> FromMdf(input, outputIso, onProgress, onLog),
            ".nrg"          => FromNrg(input, outputIso, onProgress, onLog),
            _ => throw new NotSupportedException(
                    $"Unsupported image format '{ext}'. Supported: .iso, .cue/.bin, .mds/.mdf, .nrg"),
        };
    }

    // --- .iso: nothing to convert, just copy the bytes. ----------------------
    private static ConvertResult CopyThrough(string input, string output,
        Action<long, long>? onProgress, Action<string>? onLog)
    {
        onLog?.Invoke("Input is already an ISO — copying.");
        long len = new FileInfo(input).Length;
        using (var src = File.OpenRead(input))
        using (var dst = File.Create(output))
        {
            var buf = new byte[1 << 20];
            long done = 0; int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            { dst.Write(buf, 0, n); done += n; onProgress?.Invoke(done, len); }
        }
        return new ConvertResult("ISO", len / IsoSector, IsoSector, len);
    }

    // --- .cue/.bin: reuse the burn path's BIN reader. ------------------------
    private static ConvertResult FromBinCue(string cuePath, string output,
        Action<long, long>? onProgress, Action<string>? onLog)
    {
        var cue = CueSheetParser.Parse(cuePath);
        if (!cue.IsSingleDataTrack)
            throw new NotSupportedException(
                "convert supports single-data-track BIN/CUE only (MODE1/2048 or MODE1/2352). " +
                "Mixed-mode and audio BIN/CUE aren't ISO-convertible.");
        var t = cue.Tracks[0];
        if (!File.Exists(cue.BinFile))
            throw new FileNotFoundException($"BIN referenced by cue not found: {cue.BinFile}");
        onLog?.Invoke($"BIN/CUE MODE1/{t.SectorBytes}, source {Path.GetFileName(cue.BinFile)}");

        using var body = new BinCueImageStream(cue.BinFile, t.Mode, t.SectorBytes);
        long total = body.Length;
        using (var dst = File.Create(output))
        {
            var buf = new byte[IsoSector * 32];
            long done = 0; int n;
            while ((n = body.Read(buf, 0, buf.Length)) > 0)
            { dst.Write(buf, 0, n); done += n; onProgress?.Invoke(done, total); }
        }
        return new ConvertResult($"BIN/CUE (MODE1/{t.SectorBytes})", total / IsoSector, t.SectorBytes, total);
    }

    // --- .mdf/.mds: MDF body is raw CD/DVD sectors; MDS is only a sidecar. ---
    private static ConvertResult FromMdf(string input, string output,
        Action<long, long>? onProgress, Action<string>? onLog)
    {
        // Accept either the .mdf or its .mds sidecar; the data lives in the .mdf.
        var mdf = Path.ChangeExtension(input, ".mdf");
        if (!File.Exists(mdf))
            throw new FileNotFoundException(
                $"MDF data file not found next to the MDS: expected {Path.GetFileName(mdf)}");
        onLog?.Invoke($"Alcohol/DAEMON MDF, source {Path.GetFileName(mdf)}");
        return FromRawBody(mdf, 0, new FileInfo(mdf).Length, "MDF", output, onProgress, onLog);
    }

    // --- .nrg: data track is the file up to the footer/chunk section. --------
    private static ConvertResult FromNrg(string input, string output,
        Action<long, long>? onProgress, Action<string>? onLog)
    {
        long dataEnd = NrgDataEnd(input, out string ver);
        onLog?.Invoke($"Nero NRG ({ver}), data region 0..{dataEnd:N0}");
        return FromRawBody(input, 0, dataEnd, $"NRG ({ver})", output, onProgress, onLog);
    }

    // Find where an NRG's data ends: the footer points at the trailing chunk
    // section. v2 footer = "NER5" + int64 offset (last 12 bytes); v1 footer =
    // "NERO" + int32 offset (last 8 bytes).
    private static long NrgDataEnd(string nrgPath, out string version)
    {
        using var fs = File.OpenRead(nrgPath);
        long len = fs.Length;
        if (len >= 12)
        {
            var tail = new byte[12];
            fs.Seek(len - 12, SeekOrigin.Begin);
            ReadExactly(fs, tail, 12);
            if (tail[0] == (byte)'N' && tail[1] == (byte)'E' && tail[2] == (byte)'R' && tail[3] == (byte)'5')
            {
                version = "v2/NER5";
                long off = ReadBE64(tail, 4);
                if (off > 0 && off < len) return off;
            }
        }
        if (len >= 8)
        {
            var tail = new byte[8];
            fs.Seek(len - 8, SeekOrigin.Begin);
            ReadExactly(fs, tail, 8);
            if (tail[0] == (byte)'N' && tail[1] == (byte)'E' && tail[2] == (byte)'R' && tail[3] == (byte)'O')
            {
                version = "v1/NERO";
                long off = ReadBE32(tail, 4);
                if (off > 0 && off < len) return off;
            }
        }
        throw new InvalidDataException(
            "Not a recognized NRG (no NERO/NER5 footer). If it's a plain image, rename it .iso or .bin.");
    }

    // --- core: pull 2048-byte user data out of a raw CD/DVD sector body. -----
    private static ConvertResult FromRawBody(string dataFile, long start, long end,
        string fmtLabel, string output,
        Action<long, long>? onProgress, Action<string>? onLog)
    {
        using var src = File.OpenRead(dataFile);
        src.Seek(start, SeekOrigin.Begin);

        var (sectorSize, userOffset) = DetectLayout(src, start);
        long bodyLen = end - start;
        long sectors = bodyLen / sectorSize;
        if (sectors <= 0)
            throw new InvalidDataException($"{fmtLabel}: no whole sectors in the data region.");
        if (sectorSize != IsoSector)
            onLog?.Invoke($"  raw {sectorSize}-byte sectors — extracting 2048-byte payload (offset {userOffset}).");

        long total = sectors * IsoSector;
        var sector = new byte[sectorSize];
        using (var dst = File.Create(output))
        {
            src.Seek(start, SeekOrigin.Begin);
            long written = 0;
            for (long i = 0; i < sectors; i++)
            {
                ReadExactly(src, sector, sectorSize);
                dst.Write(sector, userOffset, IsoSector);
                written += IsoSector;
                if ((i & 0x3FF) == 0) onProgress?.Invoke(written, total);
            }
            onProgress?.Invoke(total, total);
        }
        return new ConvertResult(fmtLabel, sectors, sectorSize, total);
    }

    // Detect (sectorSize, userDataOffset) from the first sector's leading bytes.
    //   cooked        → no sync           → 2048, offset 0
    //   raw Mode 1    → sync, mode byte 1 → 2352, offset 16
    //   raw Mode 2/F1 → sync, mode byte 2 → 2352, offset 24 (8-byte subheader)
    private static (int SectorSize, int UserOffset) DetectLayout(FileStream src, long start)
    {
        var head = new byte[16];
        long saved = src.Position;
        src.Seek(start, SeekOrigin.Begin);
        int got = src.Read(head, 0, 16);
        src.Seek(saved, SeekOrigin.Begin);
        if (got >= 16 && head.AsSpan(0, 12).SequenceEqual(SyncMark))
        {
            byte mode = head[15];
            return mode == 2 ? (2352, 24) : (2352, 16);   // Mode 2 Form 1 vs Mode 1
        }
        return (IsoSector, 0);   // cooked 2048-byte data
    }

    // --- little helpers ------------------------------------------------------
    private static long ReadBE64(byte[] b, int o)
        => ((long)b[o] << 56) | ((long)b[o + 1] << 48) | ((long)b[o + 2] << 40) | ((long)b[o + 3] << 32)
         | ((long)b[o + 4] << 24) | ((long)b[o + 5] << 16) | ((long)b[o + 6] << 8) | b[o + 7];

    private static long ReadBE32(byte[] b, int o)
        => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    private static void ReadExactly(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
    }
}
