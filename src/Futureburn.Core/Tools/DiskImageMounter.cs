using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Futureburn.Core.Tools;

// Mount / unmount a disc image as a drive — Daemon Tools' headline feature,
// minus the kernel driver. Windows has mounted ISO/VHD natively since Win8 via
// the documented Virtual Disk / Storage APIs — a wrapper over a documented OS
// API, not a signed SCSI driver. We drive it through the built-in Storage
// cmdlets, which are always present on Windows 11.
//
// Only ISO/VHD/VHDX mount directly; other image formats (BIN/CUE, MDF/MDS, NRG)
// are converted to a temp ISO first by the caller, then mounted.

[SupportedOSPlatform("windows")]
public static class DiskImageMounter
{
    /// <summary>Formats Windows can mount natively without conversion.</summary>
    public static bool IsNativelyMountable(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".iso" or ".img" or ".vhd" or ".vhdx";
    }

    /// <summary>Mount an image; returns the assigned drive letter (e.g. 'H').</summary>
    public static char Mount(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}");
        var full = Path.GetFullPath(imagePath);

        // -PassThru gives the image object; Get-Volume yields the drive letter.
        var script =
            "$ErrorActionPreference='Stop';" +
            $"$m = Mount-DiskImage -ImagePath {PsQuote(full)} -PassThru;" +
            "$v = $null; for ($i=0; $i -lt 20 -and -not $v.DriveLetter; $i++) " +
            "{ Start-Sleep -Milliseconds 150; $v = $m | Get-Volume };" +
            "$v.DriveLetter";
        var (rc, output, err) = RunPowerShell(script);
        if (rc != 0)
            throw new InvalidOperationException($"Mount failed: {Trim(err, output)}");
        var letter = output.Trim();
        if (string.IsNullOrEmpty(letter))
            throw new InvalidOperationException(
                "Image mounted but Windows assigned no drive letter (unformatted or unsupported image?).");
        char L = char.ToUpperInvariant(letter[0]);
        // Record the mount so we can unmount by drive letter later — Windows
        // exposes no clean "which image backs drive X:" reverse lookup (a mounted
        // ISO is just a "Microsoft Virtual DVD-ROM"), so we track our own.
        MountState.Record(L, full);
        return L;
    }

    /// <summary>Unmount by the image path that was mounted.</summary>
    public static void UnmountByImage(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);
        DismountImage(full);
        MountState.ForgetByPath(full);
    }

    /// <summary>
    /// Unmount whatever image futureburn mounted onto a drive letter, using the
    /// mount ledger. (Images mounted by something other than futureburn have no
    /// reliable letter→path reverse lookup on Windows — unmount those by path.)
    /// </summary>
    public static void UnmountByLetter(char driveLetter)
    {
        char L = char.ToUpperInvariant(driveLetter);
        var path = MountState.PathFor(L)
            ?? throw new InvalidOperationException(
                $"futureburn has no record of mounting {L}: — unmount it by image path instead " +
                "(only images mounted via futureburn can be unmounted by drive letter).");
        DismountImage(path);
        MountState.ForgetByPath(path);
    }

    private static void DismountImage(string fullPath)
    {
        var (rc, _, err) = RunPowerShell(
            $"$ErrorActionPreference='Stop'; Dismount-DiskImage -ImagePath {PsQuote(fullPath)} | Out-Null");
        if (rc != 0) throw new InvalidOperationException($"Unmount failed: {err.Trim()}");
        // Clean up a temp ISO we created for a convert-then-mount, now that it's
        // no longer in use.
        if (IsFutureburnTemp(fullPath)) { try { File.Delete(fullPath); } catch { } }
    }

    private static bool IsFutureburnTemp(string path) =>
        path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).StartsWith("futureburn-mount-", StringComparison.OrdinalIgnoreCase);

    // Single-quote a path for PowerShell (double any embedded single quotes).
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string Trim(string err, string output)
    {
        var e = err.Trim();
        return e.Length > 0 ? e : output.Trim();
    }

    // A tiny persisted ledger of futureburn's own mounts (drive letter → image
    // path), so `unmount <letter>` works even though Windows offers no reverse
    // lookup. Lives in %LOCALAPPDATA%\futureburn\mounts.json.
    private static class MountState
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "futureburn", "mounts.json");

        private static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                           ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new(StringComparer.OrdinalIgnoreCase);
        }

        private static void Save(Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(d));
            }
            catch { }
        }

        public static void Record(char letter, string path)
        {
            var d = Load(); d[letter.ToString()] = path; Save(d);
        }

        public static string? PathFor(char letter)
            => Load().TryGetValue(letter.ToString(), out var p) ? p : null;

        public static void ForgetByPath(string path)
        {
            var d = Load();
            foreach (var k in d.Where(kv => string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase))
                               .Select(kv => kv.Key).ToList())
                d.Remove(k);
            Save(d);
        }
    }

    private static (int Code, string Out, string Err) RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Couldn't start powershell.exe");
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }
}
