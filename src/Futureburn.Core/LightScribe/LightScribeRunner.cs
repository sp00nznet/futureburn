using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using static Futureburn.Core.LightScribe.LightScribeNative;

namespace Futureburn.Core.LightScribe;

// High-level helpers around the flat LSPrintAPI bindings:
//   - Drive enumeration / detection
//   - Image preparation: any Bitmap-readable format → 24-bit square BMP
//     centered on the disc's label area (LSPrintAPI.dll only consumes BMP)
//   - Submit + poll-until-done with a progress callback
//
// Why not use the C++ `lightscribe_cxx` API for callback-driven progress?
// It would require a C++/CLI shim or a hand-written C wrapper. The flat
// C surface plus 1-Hz `get_print_status` polling is what every working
// open-source consumer (CDBurnerXP, Nero) uses, and it's a 120-line
// implementation total. Stick with simple.

[SupportedOSPlatform("windows")]
public sealed class LightScribeRunner
{
    public sealed record Drive(uint Index, string DrivePath, string DisplayName, LSDriveStatus Status);

    public sealed record Status(
        LSPrintStatusCode Code,
        uint CurrentCopy,
        uint TotalCopies,
        uint PercentComplete,
        uint SecondsRemaining,
        string StatusText,
        string TimeRemainingText);

    public LightScribeRunner() => EnsureLoaded();

    /// <summary>
    /// True when at least one LightScribe-capable drive is connected and the
    /// LSS service is responding.
    /// </summary>
    public bool AnyDrivePresent()
    {
        Check(have_lightscribe_drive(out bool any), nameof(have_lightscribe_drive));
        return any;
    }

    public IReadOnlyList<Drive> EnumerateDrives()
    {
        Check(get_drive_count(out uint count), nameof(get_drive_count));
        var drives = new List<Drive>();
        for (uint i = 0; i < count; i++)
        {
            var pathBuf = new StringBuilder(260);
            Check(get_drive_path(i, pathBuf, (uint)pathBuf.Capacity), nameof(get_drive_path));
            var nameBuf = new StringBuilder(260);
            Check(get_drive_display_name(i, nameBuf, (uint)nameBuf.Capacity), nameof(get_drive_display_name));
            Check(get_drive_status(i, out var status), nameof(get_drive_status));
            drives.Add(new Drive(i, pathBuf.ToString(), nameBuf.ToString(), status));
        }
        return drives;
    }

    /// <summary>
    /// Map a Windows drive letter (e.g. "F:" or "F:\\") to the SDK's drive
    /// index. Returns null if the letter isn't a LightScribe drive.
    /// </summary>
    public uint? FindDriveIndex(string driveLetter)
    {
        var letter = driveLetter.TrimEnd('\\', '/').TrimEnd(':').ToUpperInvariant();
        foreach (var d in EnumerateDrives())
        {
            var path = d.DrivePath.TrimEnd('\\', '/').TrimEnd(':').ToUpperInvariant();
            if (path == letter) return d.Index;
        }
        return null;
    }

    public Status GetStatus(uint driveIndex)
    {
        var statusBuf = new StringBuilder(256);
        var timeBuf   = new StringBuilder(64);
        Check(get_print_status(driveIndex, out var code,
            out uint curCopy, out uint total, out uint pct, out uint secs,
            statusBuf, (uint)statusBuf.Capacity,
            timeBuf,   (uint)timeBuf.Capacity), nameof(get_print_status));
        return new Status(code, curCopy, total, pct, secs, statusBuf.ToString(), timeBuf.ToString());
    }

    public void Abort(uint driveIndex)
    {
        Check(abort_print(driveIndex), nameof(abort_print));
    }

    public enum Quality { Draft, Normal, Best }

    /// <summary>
    /// Submit a label-print job to the chosen drive and block, polling status
    /// every <paramref name="pollIntervalMs"/> milliseconds and forwarding
    /// updates to <paramref name="onProgress"/>. Throws on failure.
    /// </summary>
    /// <param name="bmpPath">A 24-bit Windows BMP. Use
    /// <see cref="PrepareLabelImage"/> to convert from PNG/JPG/etc. first.</param>
    public Status PrintAndWait(
        uint driveIndex,
        string bmpPath,
        Quality quality = Quality.Best,
        int copies = 1,
        bool showOperatorDialog = false,
        int pollIntervalMs = 1000,
        Action<Status>? onProgress = null,
        CancellationToken cancellation = default)
    {
        if (!File.Exists(bmpPath))
            throw new FileNotFoundException("Label BMP not found", bmpPath);
        if (!Path.GetExtension(bmpPath).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"LSPrintAPI requires a .bmp file (got {Path.GetExtension(bmpPath)}). " +
                $"Use PrepareLabelImage() to convert first.", nameof(bmpPath));

        var drives = EnumerateDrives();
        var drive  = drives.FirstOrDefault(d => d.Index == driveIndex)
                    ?? throw new ArgumentException($"No LightScribe drive at index {driveIndex}.", nameof(driveIndex));

        // launch_printing_dialog uses a single command-line-style options
        // string. Quote the filename in case the path has spaces.
        var qualityArg = quality switch
        {
            Quality.Draft  => "draft",
            Quality.Normal => "normal",
            _              => "best",
        };
        // We use launch_print_options_dialog (the user-driven LSS UI) rather
        // than launch_printing_dialog (the programmatic-args dialog). The
        // programmatic path's boost::program_options parser is stuck on this
        // LSS build — every drive-identifier form (--path F, F:, F:\, --index 0,
        // --name "<display>") returns either "invalid drive name argument" or
        // "invalid path argument", even with a real BMP at a known path and
        // the correct boost-style flag values. Until we figure out the right
        // form, the user-driven dialog gets the burn done: LSS shows its UI,
        // the user can confirm file + drive + quality + click Print, and the
        // service handles the actual label burn the same way.
        //
        // For reference, the allowed options on this LSS build (1.18.27.10):
        //   -f --filename arg              image file name (string)
        //   -i --index    arg (=0)         drive index (uint)
        //   -n --name     arg              drive name (string)
        //   -p --path     arg              drive path (string)
        //   -q --quality  arg (=best)      draft / normal / best
        //   -c --copies   arg (=1)         copies (uint)
        //   ... plus a number of bool flags requiring explicit 0/1 values.
        // The dialog rejects every drive-identifier form we've tried, even
        // values that exactly match what get_drive_path() returns. Likely
        // requires a registered-drive-context the LSS service hasn't loaded
        // for our process. Revisit when we have the LSS source / a working
        // reference.
        if (bmpPath.Contains(' '))
            throw new ArgumentException(
                $"BMP path '{bmpPath}' contains spaces. boost::program_options inside " +
                $"LSPrintingDialog.exe doesn't unquote string values, so spaces in the " +
                $"path break the parse. Place the BMP at a space-free temp path.",
                nameof(bmpPath));

        // launch_print_options_dialog has a SMALLER accepted-arg set than
        // launch_printing_dialog — it rejects --quality / --copies as
        // "unknown option" because the user picks those in the UI. Just
        // hand it the filename; everything else is user-driven.
        var opts = "--filename " + bmpPath;

        Check(launch_print_options_dialog(opts), nameof(launch_print_options_dialog));

        // Poll until we hit a terminal state, surfacing each status update.
        // Most of the wall time is spent in Printing; the LSS service drives
        // the laser and the pct/secs values come from there.
        Status last;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            last = GetStatus(driveIndex);
            onProgress?.Invoke(last);
            if (last.Code is LSPrintStatusCode.Complete
                          or LSPrintStatusCode.Canceled
                          or LSPrintStatusCode.GenericError) break;
            Thread.Sleep(pollIntervalMs);
        }

        if (last.Code == LSPrintStatusCode.GenericError)
            throw new InvalidOperationException(
                $"LightScribe label burn failed: {last.StatusText} " +
                $"(at {last.PercentComplete}%, code {last.Code})");
        return last;
    }

    /// <summary>
    /// Convert any Bitmap-readable image (PNG, JPG, BMP, GIF, TIFF) to a
    /// 24-bit square Windows BMP that LSPrintAPI accepts. Centers the source
    /// image into a square canvas of <paramref name="canvasSize"/> pixels and
    /// fills the background with white (the unburned LightScribe coating
    /// reads as off-white, so white = "leave alone"). Returns the path of
    /// the temp BMP that was written.
    /// </summary>
    public static string PrepareLabelImage(string sourcePath, int canvasSize = 800)
    {
        if (canvasSize is < 100 or > 4000)
            throw new ArgumentOutOfRangeException(nameof(canvasSize),
                "Canvas size should be between 100 and 4000 px (LightScribe SDK accepts 800–2772 typical).");

        using var src = System.Drawing.Image.FromFile(sourcePath);

        // Center-fit into a square canvas, preserving aspect.
        var canvas = new Bitmap(canvasSize, canvasSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.InterpolationMode  = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode      = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            // Scale to fit the smaller dimension into the square, center it.
            float scale = Math.Min((float)canvasSize / src.Width, (float)canvasSize / src.Height);
            int w = (int)(src.Width * scale);
            int h = (int)(src.Height * scale);
            int x = (canvasSize - w) / 2;
            int y = (canvasSize - h) / 2;
            g.DrawImage(src, x, y, w, h);
        }

        var outPath = Path.Combine(
            Path.GetTempPath(),
            $"futureburn-lslabel-{Guid.NewGuid():N}.bmp");
        canvas.Save(outPath, ImageFormat.Bmp);
        canvas.Dispose();
        return outPath;
    }

    private static void Check(int rc, string call)
    {
        if (rc != LS_SUCCESS)
            throw new InvalidOperationException(
                $"LightScribe call '{call}' failed: {DescribeReturn(rc)}");
    }
}
