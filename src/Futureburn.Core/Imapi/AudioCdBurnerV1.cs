using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Futureburn.Core.Audio;

namespace Futureburn.Core.Imapi;

// IMAPI v1 audio CD burn path — the legacy Win XP-era API. Used as a fallback
// for drives whose firmware doesn't cooperate with IMAPI v2's TAO PrepareMedia
// (the LG GE20LU10 / FE06 is the known case).
//
// The v1 audio model is "blocks" (one block = one 2352-byte CD-DA frame, same
// as a sector). The burn flow is:
//   1. CoCreate MsDiscMasterObj
//   2. master.Open()
//   3. master.SetActiveDiscMasterFormat(IID_IRedbookDiscMaster) -> IRedbookDiscMaster
//   4. master.EnumDiscRecorders, find the right one, master.SetActiveDiscRecorder()
//   5. for each track: redbook.CreateAudioTrack(blocks), AddAudioTrackBlocks (chunks),
//      CloseAudioTrack
//   6. master.RecordDisc(simulate, ejectAfterBurn)
//   7. master.Close()

[SupportedOSPlatform("windows")]
public static class AudioCdBurnerV1
{
    public sealed record DiagnosticReport(
        bool MasterOpened,
        int RecorderCount,
        IReadOnlyList<string> RecorderPaths,
        bool RedbookFormatAvailable,
        int? AudioBlockSize,
        int? TotalAudioBlocks,
        int? AvailableAudioBlocks,
        string? Error);

    public sealed record V1BurnPlan(
        OpticalDrive Drive,
        IReadOnlyList<AudioCdBurner.TrackPlan> Tracks,
        int TotalBlocks,
        int AvailableBlocks,
        int BlockSize,
        string TempDir)
    {
        public TimeSpan TotalDuration =>
            TimeSpan.FromSeconds(TotalBlocks / 75.0);
    }

    /// <summary>
    /// Smoke-test: does IMAPI v1 even work on this machine? Returns a diagnostic
    /// describing what we found. No disc state is changed.
    /// </summary>
    public static DiagnosticReport Diagnose()
    {
        IDiscMaster? master = null;
        try
        {
            master = (IDiscMaster)new MsDiscMasterObjClass();
            master.Open();

            var paths = new List<string>();
            try
            {
                master.EnumDiscRecorders(out var recEnum);
                var batch = new IDiscRecorder[1];
                while (recEnum.Next(1, batch, out var fetched) == 0 && fetched > 0)
                {
                    try
                    {
                        batch[0].GetPath(out var p);
                        paths.Add(p ?? "(no path)");
                    }
                    catch { paths.Add("(GetPath threw)"); }
                    Marshal.ReleaseComObject(batch[0]);
                    batch[0] = null!;
                }
            }
            catch (Exception ex)
            {
                return new DiagnosticReport(true, 0, paths, false, null, null, null,
                    $"EnumDiscRecorders failed: {ex.Message}");
            }

            // Try to switch to Redbook (audio CD) format and read its props.
            int? blockSize = null, totalBlocks = null, availBlocks = null;
            bool redbookOk = false;
            if (paths.Count > 0)
            {
                try
                {
                    var formatId = ImapiV1Formats.Redbook;
                    master.SetActiveDiscMasterFormat(ref formatId, out var formatObj);
                    var redbook = (IRedbookDiscMaster)formatObj;
                    redbookOk = true;
                    try { redbook.GetAudioBlockSize(out int bs); blockSize = bs; } catch { }
                    try { redbook.GetTotalAudioBlocks(out int tb); totalBlocks = tb; } catch { }
                    try { redbook.GetAvailableAudioTrackBlocks(out int ab); availBlocks = ab; } catch { }
                    Marshal.ReleaseComObject(redbook);
                }
                catch (Exception ex)
                {
                    return new DiagnosticReport(true, paths.Count, paths, false, null, null, null,
                        $"SetActiveDiscMasterFormat(Redbook) failed: {ex.Message}");
                }
            }

            return new DiagnosticReport(true, paths.Count, paths, redbookOk, blockSize, totalBlocks, availBlocks, null);
        }
        catch (Exception ex)
        {
            return new DiagnosticReport(false, 0, Array.Empty<string>(), false, null, null, null,
                $"IMAPI v1 setup failed: {ex.Message}");
        }
        finally
        {
            if (master is not null)
            {
                try { master.Close(); } catch { }
                Marshal.ReleaseComObject(master);
            }
        }
    }

    public static V1BurnPlan Plan(
        OpticalDrive drive,
        Audio.Playlist playlist,
        string tempDir)
    {
        // 1. Decode (or pass through) tracks — same logic as v2's AudioCdBurner.
        Directory.CreateDirectory(tempDir);
        var trackPlans = new List<AudioCdBurner.TrackPlan>();
        int idx = 1;
        foreach (var entry in playlist.Entries)
        {
            if (!File.Exists(entry.Path))
                throw new AudioCdBurner.BurnException($"Track {idx} not found on disk: {entry.Path}");

            AudioInfo info = AudioDecoder.Probe(entry.Path);
            string burnPath;
            bool decoded;
            if (info.IsCdFormat &&
                Path.GetExtension(entry.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                burnPath = entry.Path;
                decoded  = false;
            }
            else
            {
                burnPath = Path.Combine(tempDir, $"track-{idx:00}.wav");
                AudioDecoder.DecodeToCdWav(entry.Path, burnPath);
                decoded = true;
                info    = AudioDecoder.Probe(burnPath);
            }

            var sectors = CdFormat.SectorsForDuration(info.Duration);
            if (sectors < 300)
                throw new AudioCdBurner.BurnException(
                    $"Track {idx} ({info.Duration.TotalSeconds:0.0}s) is shorter than the CD minimum (4 seconds).");

            trackPlans.Add(new AudioCdBurner.TrackPlan(
                Index:          idx,
                SourcePath:     entry.Path,
                BurnPath:       burnPath,
                RequiredDecode: decoded,
                Duration:       info.Duration,
                Sectors:        sectors,
                Title:          entry.Title));
            idx++;
        }

        // 2. Query disc via v1.
        var diag = Diagnose();
        if (!diag.MasterOpened || diag.RecorderCount == 0)
            throw new AudioCdBurner.BurnException(
                $"IMAPI v1 isn't usable on this machine: {diag.Error ?? "no recorders enumerated"}");
        if (!diag.RedbookFormatAvailable)
            throw new AudioCdBurner.BurnException(
                $"IMAPI v1 Redbook (audio CD) format is unavailable: {diag.Error ?? "unknown reason"}");
        if (diag.AvailableAudioBlocks is null)
            throw new AudioCdBurner.BurnException("IMAPI v1 couldn't report available block count.");

        long totalSectors = trackPlans.Sum(t => t.Sectors);
        if (totalSectors > diag.AvailableAudioBlocks.Value)
            throw new AudioCdBurner.BurnException(
                $"Tracks need {totalSectors:N0} blocks but disc has only {diag.AvailableAudioBlocks.Value:N0} available.");

        return new V1BurnPlan(
            Drive:            drive,
            Tracks:           trackPlans,
            TotalBlocks:      diag.TotalAudioBlocks ?? 0,
            AvailableBlocks:  diag.AvailableAudioBlocks ?? 0,
            BlockSize:        diag.AudioBlockSize     ?? 2352,
            TempDir:          tempDir);
    }

    public static void ExecuteBurn(V1BurnPlan plan, Action<int, int>? onTrackStart = null)
    {
        IDiscMaster? master = null;
        try
        {
            master = (IDiscMaster)new MsDiscMasterObjClass();
            master.Open();

            // Quirk discovered the hard way: explicitly calling SetActiveDiscRecorder
            // makes RecordDisc fail with IMAPI_E_NOACTIVERECORDER (0x8004020E),
            // which is the opposite of what you'd expect from the docs. Diagnose()
            // works without ever calling SetActiveDiscRecorder, so IMAPI v1 must
            // be picking an implicit default. We let it. (When the user has multiple
            // writers we'll need to revisit — Windows always has at least one
            // optical writer in the default-recorder slot if any are attached.)
            var formatId = ImapiV1Formats.Redbook;
            master.SetActiveDiscMasterFormat(ref formatId, out var formatObj);
            var redbook = (IRedbookDiscMaster)formatObj;

            // We still enumerate so we can keep a strong reference and (in future)
            // pass through to a richer diagnostic on failure.
            master.EnumDiscRecorders(out var recEnum);
            var batch = new IDiscRecorder[1];
            if (recEnum.Next(1, batch, out _) != 0)
                throw new AudioCdBurner.BurnException("IMAPI v1 enumerated zero recorders.");
            var recorder = batch[0];

            redbook.GetAudioBlockSize(out int blockSize);
            // Read in chunks of 64 blocks (~150 KB) — large enough for throughput,
            // small enough for responsive cancel/progress later.
            int chunkBlocks = 64;
            var buffer = new byte[blockSize * chunkBlocks];

            int n = 1;
            foreach (var track in plan.Tracks)
            {
                onTrackStart?.Invoke(n, plan.Tracks.Count);
                using var padded = new CdPaddedAudioStream(track.BurnPath);
                long bytesTotal = padded.Length;  // already a multiple of blockSize (2352)
                int totalBlocks = (int)(bytesTotal / blockSize);

                redbook.CreateAudioTrack(totalBlocks);

                long bytesRemaining = bytesTotal;
                while (bytesRemaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, bytesRemaining);
                    int got  = ReadFully(padded, buffer, want);
                    if (got == 0) break;
                    redbook.AddAudioTrackBlocks(buffer, got);
                    bytesRemaining -= got;
                }

                redbook.CloseAudioTrack();
                n++;
            }

            // Real burn (not simulate). Eject when done.
            master.RecordDisc(bSimulate: false, bEjectAfterBurn: true);

            Marshal.ReleaseComObject(redbook);
            Marshal.ReleaseComObject(recorder);
        }
        catch (COMException ex)
        {
            throw new AudioCdBurner.BurnException(
                $"IMAPI v1 burn failed (HRESULT 0x{(uint)ex.HResult:X8}): {ex.Message}", ex);
        }
        finally
        {
            if (master is not null)
            {
                try { master.Close(); } catch { }
                Marshal.ReleaseComObject(master);
            }
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
