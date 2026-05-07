using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Futureburn.Core.Spti;

// Win32 P/Invoke for SCSI Pass-Through Interface (SPTI).
//
// SPTI lets us send raw SCSI MMC commands to an optical drive via DeviceIoControl,
// completely bypassing IMAPI. This is what ImgBurn uses. It gives us absolute
// control over what the drive does, at the cost of writing every protocol step
// ourselves. Worth it for drives where IMAPI has compatibility quirks.
//
// The drive is opened by path like "\\.\F:" with read+write access. Then we send
// IOCTL_SCSI_PASS_THROUGH_DIRECT, which takes a SCSI_PASS_THROUGH_DIRECT struct
// describing the command (CDB), data direction, data buffer, and timeout.

[SupportedOSPlatform("windows")]
internal static class SptiNative
{
    public const uint GENERIC_READ  = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ  = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;

    // SCSI_IOCTL_DATA direction codes.
    public const byte SCSI_IOCTL_DATA_OUT          = 0;
    public const byte SCSI_IOCTL_DATA_IN           = 1;
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED  = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    // SCSI_PASS_THROUGH_DIRECT from ntddscsi.h. Natural alignment (no Pack
    // override): 44 bytes on x86, 56 bytes on x64. Mismatching the size makes
    // DeviceIoControl return ERROR_REVISION_MISMATCH (Win32 1306).
    [StructLayout(LayoutKind.Sequential)]
    public struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    // Wrapper block: Spt + 4-byte filler + 32-byte sense buffer, matching the
    // standard SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER pattern from MSDN samples.
    [StructLayout(LayoutKind.Sequential)]
    public struct ScsiPassThroughDirectWithBuffer
    {
        public ScsiPassThroughDirect Spt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseBuf;
    }
}
