using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Futureburn.Core.Fs;

// Build an ISO 9660 / Joliet / UDF disc image from a folder of files using
// Windows' built-in IMAPI 2 File System (IMAPI2FS) COM library. We treat it
// the same way we treat IMAPI v2 itself — late-bound via dynamic, no NuGet
// wrappers. The output is a sequential byte stream that's a valid ISO image
// for the chosen file systems; we either save it to a .iso file or feed it
// straight to a burn engine.
//
// File system choices (combine via flags):
//   ISO 9660  — universal compat. ASCII filenames, 8.3 limits.
//   Joliet    — Unicode + long filenames overlay on ISO 9660. Windows + many DVD players read it.
//   UDF       — required for DVD-Video and Blu-ray, supports very large files. UDF 1.02 is
//               the DVD-Video baseline; DVD-RAM uses UDF 1.5; Blu-ray uses UDF 2.50.
//
// For maximum compatibility on a data CD/DVD: enable all three. For DVD-Video
// or Blu-ray: UDF only (or UDF + ISO 9660 fallback).
//
// IMAPI2FS does the heavy lifting — Joliet name escaping, UDF directory
// records, allocation tables, the works. We just pick the file systems,
// add the tree, and read out the bytes.

[SupportedOSPlatform("windows")]
public static class FsImageBuilder
{
    [Flags]
    public enum FileSystem
    {
        // Values must match IMAPI2FS's FsiFileSystems enum (imapi2fs.idl).
        None     = 0x00,
        Iso9660  = 0x01,
        Joliet   = 0x02,
        Udf      = 0x04,
        Unknown  = 0x40000000,
        All      = Iso9660 | Joliet | Udf,
    }

    public sealed record BuildResult(long TotalBytes, int BlockSize, long BlockCount);

    /// <summary>
    /// Build a disc image from <paramref name="sourceFolder"/> and write it as
    /// a sequential byte stream to <paramref name="output"/>. Returns the
    /// total bytes written.
    /// </summary>
    public static BuildResult Build(
        string sourceFolder,
        Stream output,
        string volumeLabel,
        FileSystem fileSystems = FileSystem.All,
        Action<long, long>? onProgress = null)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

        var fsiType = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")
            ?? throw new InvalidOperationException(
                "IMAPI2FS isn't registered on this system. (Should be present on Win10/11.)");

        dynamic fsi = Activator.CreateInstance(fsiType)!;
        try
        {
            fsi.FileSystemsToCreate = (int)fileSystems;

            // Volume label: ISO 9660 caps at 32 chars, must be ASCII-clean for
            // strict ISO. IMAPI handles transliteration; we just trim.
            var label = volumeLabel ?? "FUTUREBURN";
            if (label.Length > 32) label = label.Substring(0, 32);
            fsi.VolumeName = label;

            // Add the folder's CONTENTS to the root (includeBaseDir=false).
            // Passing true would create a subdirectory matching the folder name
            // at root, which is rarely what you want.
            dynamic root = fsi.Root;
            try
            {
                root.AddTree(sourceFolder, false);
            }
            finally { Marshal.ReleaseComObject(root); }

            dynamic result = fsi.CreateResultImage();
            try
            {
                int blockSize    = Convert.ToInt32(result.BlockSize);
                long totalBlocks = Convert.ToInt64(result.TotalBlocks);
                long totalBytes  = blockSize * totalBlocks;

                IStream comStream = (IStream)result.ImageStream;
                try
                {
                    CopyComStreamToOutput(comStream, output, totalBytes, onProgress);
                }
                finally { Marshal.ReleaseComObject(comStream); }

                return new BuildResult(totalBytes, blockSize, totalBlocks);
            }
            finally { Marshal.ReleaseComObject(result); }
        }
        finally { Marshal.FinalReleaseComObject(fsi); }
    }

    /// <summary>
    /// Convenience overload: build the image and write it to <paramref name="outputIsoPath"/>.
    /// </summary>
    public static BuildResult Build(
        string sourceFolder,
        string outputIsoPath,
        string volumeLabel,
        FileSystem fileSystems = FileSystem.All,
        Action<long, long>? onProgress = null)
    {
        using var output = File.Create(outputIsoPath);
        return Build(sourceFolder, output, volumeLabel, fileSystems, onProgress);
    }

    // Read a COM IStream sequentially into a managed Stream. Uses a moderately-
    // sized buffer (64 KB) and reports progress against the known total.
    private static void CopyComStreamToOutput(
        IStream src, Stream dst, long totalBytes, Action<long, long>? onProgress)
    {
        const int bufSize = 64 * 1024;
        var buf = new byte[bufSize];
        IntPtr bytesReadPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            long copied = 0;
            while (true)
            {
                src.Read(buf, bufSize, bytesReadPtr);
                int n = Marshal.ReadInt32(bytesReadPtr);
                if (n == 0) break;
                dst.Write(buf, 0, n);
                copied += n;
                onProgress?.Invoke(copied, totalBytes);
            }
        }
        finally { Marshal.FreeHGlobal(bytesReadPtr); }
    }
}
