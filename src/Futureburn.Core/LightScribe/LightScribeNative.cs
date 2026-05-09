using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Futureburn.Core.LightScribe;

// P/Invoke bindings for LSPrintAPI.dll — the LightScribe Public SDK's flat C
// API. Eight exported functions, all stable across the SDK's lifetime
// (1.x.x, never went 2.0). Source-truth references:
//   - readkong mirror of HP's official PDF (function signatures + status codes)
//     https://www.readkong.com/page/lightscribe-public-windows-software-development-kit-9286980
//   - qlscribe — open-source GPL Qt client of the C++ SDK
//     https://github.com/kruszewskia/qlscribe
//   - CDBurnerXP KB5 — confirms the registry-key + LoadLibrary loader pattern
//     https://cdburnerxp.se/help/kb/5
//
// Important constraints:
//   1. LSPrintAPI.dll is 32-bit only. The hosting process MUST be x86 — our
//      Cli and Gui csproj files set PlatformTarget=x86 for this reason.
//   2. The LightScribeService Windows service must be running (it serializes
//      drive access between processes; the DLL talks to it via RPC).
//   3. The DLL is registered in the 32-bit registry hive at
//      HKLM\SOFTWARE\WOW6432Node\LightScribe\LSPrintAPI on x64 Windows.
//      Our static ctor reads that key (or falls back to the well-known LSS
//      install path) and pre-loads the DLL via NativeLibrary.Load.

// Public enums are at namespace scope (not nested in the internal P/Invoke
// class) so public records like LightScribeRunner.Drive can carry them.
public enum LSDriveStatus
{
    Available    = 0,
    Error        = 1,
    Update       = 2,
    Busy         = 3,
    Unknown      = 4,
}

public enum LSPrintStatusCode
{
    Unavailable     = 0,
    Starting        = 1,
    Preparing       = 2,
    Detecting       = 3,
    DriveStartUp    = 4,
    Printing        = 5,
    Complete        = 6,
    Canceled        = 7,
    Canceling       = 8,
    GenericError    = 9,
}

[SupportedOSPlatform("windows")]
internal static class LightScribeNative
{
    public const int LS_SUCCESS              = 0;
    public const int LS_FAILURE              = 1;
    public const int LS_INVALID_DRIVE_INDEX  = 2;
    public const int LS_INVALID_ARRAY_SIZE   = 3;

    // The DLL ships in the LSS runtime install. Path can also come from the
    // 32-bit registry hive (the SDK loader sample reads it from there). We
    // try registry first, fall back to the canonical install path on x64
    // Windows (which is where the user has it).
    private static readonly string DllPath = ResolveDllPath();

    private static string ResolveDllPath()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key  = hklm.OpenSubKey(@"SOFTWARE\LightScribe\LSPrintAPI");
            if (key?.GetValue("LSPrintAPIPath") is string p && File.Exists(p)) return p;
        }
        catch { /* fall through to default */ }

        var defaultPath = @"C:\Program Files (x86)\Common Files\LightScribe\LSPrintAPI.dll";
        return File.Exists(defaultPath)
            ? defaultPath
            : throw new FileNotFoundException(
                "LSPrintAPI.dll not found. Install the LightScribe System Software " +
                "(http://www.lightscribe.com/ — see README for current mirror) and " +
                "make sure C:\\Program Files (x86)\\Common Files\\LightScribe\\LSPrintAPI.dll exists.");
    }

    /// <summary>
    /// Pre-load the 32-bit DLL once, before any P/Invoke fires. Throws
    /// BadImageFormatException if our process is x64 (DllImport's lazy
    /// resolver gives a much less helpful error in that case).
    /// </summary>
    public static void EnsureLoaded()
    {
        if (IntPtr.Size != 4)
            throw new InvalidOperationException(
                "LightScribe requires a 32-bit (x86) host process. " +
                "LSPrintAPI.dll is 32-bit only and HP never shipped an x64 build. " +
                "If you're seeing this, the executable was compiled with the wrong " +
                "PlatformTarget. Cli and Gui csproj files must declare " +
                "<PlatformTarget>x86</PlatformTarget>.");

        // Idempotent — Win32 LoadLibrary refcount handles repeats.
        NativeLibrary.Load(DllPath);
    }

    [DllImport("LSPrintAPI.dll")]
    public static extern int have_lightscribe_drive(out bool rHaveDrive);

    [DllImport("LSPrintAPI.dll")]
    public static extern int get_drive_count(out uint rDriveCount);

    [DllImport("LSPrintAPI.dll", CharSet = CharSet.Unicode)]
    public static extern int get_drive_path(uint driveIndex, StringBuilder pPath, uint size);

    [DllImport("LSPrintAPI.dll", CharSet = CharSet.Unicode)]
    public static extern int get_drive_display_name(uint driveIndex, StringBuilder pName, uint size);

    [DllImport("LSPrintAPI.dll")]
    public static extern int get_drive_status(uint driveIndex, out LSDriveStatus rStatus);

    [DllImport("LSPrintAPI.dll", CharSet = CharSet.Unicode)]
    public static extern int launch_print_options_dialog(string pOptions);

    [DllImport("LSPrintAPI.dll", CharSet = CharSet.Unicode)]
    public static extern int launch_printing_dialog(string pOptions);

    [DllImport("LSPrintAPI.dll", CharSet = CharSet.Unicode)]
    public static extern int get_print_status(
        uint driveIndex,
        out LSPrintStatusCode code,
        out uint curCopy,
        out uint totalCopies,
        out uint pctComplete,
        out uint secsRemaining,
        StringBuilder statusStr,
        uint statusStrSize,
        StringBuilder timeRemainingStr,
        uint timeStrSize);

    [DllImport("LSPrintAPI.dll")]
    public static extern int abort_print(uint driveIndex);

    public static string DescribeReturn(int rc) => rc switch
    {
        LS_SUCCESS              => "OK",
        LS_FAILURE              => "LS_FAILURE (generic)",
        LS_INVALID_DRIVE_INDEX  => "LS_INVALID_DRIVE_INDEX",
        LS_INVALID_ARRAY_SIZE   => "LS_INVALID_ARRAY_SIZE",
        _                       => $"unknown ({rc})",
    };
}
