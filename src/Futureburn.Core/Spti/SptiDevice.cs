using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Futureburn.Core.Spti;

// High-level wrapper around an optical drive opened for SCSI Pass-Through.
//
// Usage:
//   using var dev = SptiDevice.OpenDriveLetter('F');
//   var inq = dev.Inquiry();
//   Console.WriteLine($"{inq.Vendor} {inq.Product} {inq.Revision}");
//
// This is the foundation of a future SPTI-based burn engine. The actual burn
// flow (RESERVE TRACK + WRITE 12 loops + CLOSE TRACK + CLOSE SESSION) lives
// in SptiBurnEngine, when we get to it.

[SupportedOSPlatform("windows")]
public sealed class SptiDevice : IDisposable
{
    private readonly SafeFileHandle _handle;

    public string DevicePath { get; }

    private SptiDevice(string devicePath, SafeFileHandle handle)
    {
        DevicePath = devicePath;
        _handle    = handle;
    }

    public static SptiDevice OpenDriveLetter(char letter)
    {
        var path = $@"\\.\{char.ToUpperInvariant(letter)}:";
        var handle = SptiNative.CreateFileW(
            path,
            SptiNative.GENERIC_READ | SptiNative.GENERIC_WRITE,
            SptiNative.FILE_SHARE_READ | SptiNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            SptiNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Couldn't open {path} for SCSI pass-through (Win32 error {err}). " +
                "This usually requires running elevated, or the drive is held by another process.");
        }
        return new SptiDevice(path, handle);
    }

    public void Dispose() => _handle.Dispose();

    public sealed record InquiryResult(string Vendor, string Product, string Revision);

    public sealed record TocTrack(
        int Number,
        bool IsAudio,
        bool HasPreemphasis,
        int StartLba,
        int LengthLba)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds(LengthLba / 75.0);
        public string TypeLabel => IsAudio ? "audio" + (HasPreemphasis ? " (pre-emph)" : "") : "data";
    }

    public sealed record DiscToc(
        int FirstTrackNumber,
        int LastTrackNumber,
        int LeadOutLba,
        IReadOnlyList<TocTrack> Tracks)
    {
        public TimeSpan TotalDuration => TimeSpan.FromSeconds(LeadOutLba / 75.0);
        public bool HasAudio => Tracks.Any(t => t.IsAudio);
        public bool HasData  => Tracks.Any(t => !t.IsAudio);
    }

    public enum DiscStatus  { Empty = 0, Incomplete = 1, Finalized = 2, Other = 3 }
    public enum SessionState { Empty = 0, Incomplete = 1, Reserved = 2, Complete = 3 }

    public sealed record DiscInformation(
        DiscStatus Status,
        SessionState LastSessionState,
        bool Erasable,
        int Sessions,
        int FirstTrack,
        int LastTrackInLastSession,
        byte DiscTypeCode,
        bool DiscIdValid,
        uint DiscId)
    {
        public string DiscTypeName => DiscTypeCode switch
        {
            0x00 => "CD-DA or CD-ROM",
            0x10 => "CD-i",
            0x20 => "CD-ROM XA",
            0xFF => "Undefined",
            _    => $"Unknown 0x{DiscTypeCode:X2}",
        };

        // The most user-relevant bottom-line: is this disc playable in a normal CD player?
        // "Finalized + Complete" means: yes. Anything else: probably not.
        public bool IsPlayablyFinalized => Status == DiscStatus.Finalized && LastSessionState == SessionState.Complete;
    }

    /// <summary>
    /// MMC INQUIRY (opcode 0x12) — returns standard inquiry data including
    /// vendor / product / firmware revision strings. Always supported by every
    /// SCSI device. Useful as a "does SPTI work at all?" smoke test.
    /// </summary>
    public InquiryResult Inquiry()
    {
        // 36-byte standard inquiry data response.
        var data = new byte[96];
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.Inquiry;
        cdb[4] = (byte)data.Length;  // allocation length

        SendScsi(cdb, cdbLength: 6, data, dataIn: true, timeoutSec: 10);

        // Bytes 8-15 = Vendor (8 chars, space-padded)
        // Bytes 16-31 = Product (16 chars)
        // Bytes 32-35 = Product Revision (4 chars)
        string vendor   = Encoding.ASCII.GetString(data, 8,  8).Trim();
        string product  = Encoding.ASCII.GetString(data, 16, 16).Trim();
        string revision = Encoding.ASCII.GetString(data, 32, 4).Trim();
        return new InquiryResult(vendor, product, revision);
    }

    /// <summary>
    /// MMC READ DISC INFORMATION (opcode 0x51) data type 0 — the authoritative
    /// answer to "is this disc finalized?". A disc that's `DiscStatus.Finalized`
    /// AND `SessionState.Complete` should play in any standalone CD player.
    /// Anything else (Incomplete / Empty / Damaged) means the writing software
    /// didn't close the disc, and players will likely refuse it.
    /// </summary>
    public DiscInformation ReadDiscInformation()
    {
        var data = new byte[34];
        var cdb = new byte[10];
        cdb[0] = MmcOpcodes.ReadDiscInformation;
        cdb[1] = 0x00;  // data type: 0 = standard disc information
        cdb[7] = (byte)(data.Length >> 8);
        cdb[8] = (byte)(data.Length & 0xFF);
        SendScsi(cdb, cdbLength: 10, data, dataIn: true);

        // MMC-6 layout (bytes 0-31):
        //   0-1   Disc Information Length (excluding these 2 bytes)
        //   2     Reserved (bits 7-5) | Erasable (bit 4) | State of Last Session (bits 3-2) | Disc Status (bits 1-0)
        //   3     Number of First Track on Disc (low byte)
        //   4     Number of Sessions (low byte)
        //   5     First Track Number in Last Session (low byte)
        //   6     Last Track Number in Last Session (low byte)
        //   7     DID_V/DBC_V/URU/DAC_V/Legacy/BG Format Status flags
        //   8     Disc Type (00=CD-DA/CD-ROM, 10=CD-i, 20=CD-ROM XA, FF=Undefined)
        //   9     Number of Sessions (high byte)
        //   10    First Track Number in Last Session (high byte)
        //   11    Last Track Number in Last Session (high byte)
        //   12-15 Disc Identification (32-bit)
        //
        // Disc Status:        00=Empty 01=Incomplete 10=Finalized 11=Other
        // State of Last Sess: 00=Empty 01=Incomplete 10=Reserved   11=Complete
        // A properly closed CD-R yields byte 2 = 0x0E (status 10 + LSS 11 + erasable 0).
        var status        = (DiscStatus)(data[2] & 0x03);
        bool erasable     = (data[2] & 0x10) != 0;
        var sessionState  = (SessionState)((data[2] >> 2) & 0x03);
        int firstTrack    = data[3];
        int sessions      = (data[9] << 8) | data[4];
        int lastTrack     = (data[11] << 8) | data[6];
        byte discTypeCode = data[8];
        bool didValid     = (data[7] & 0x80) != 0;
        uint discId       = (uint)((data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15]);

        return new DiscInformation(
            Status:                  status,
            LastSessionState:        sessionState,
            Erasable:                erasable,
            Sessions:                sessions,
            FirstTrack:              firstTrack,
            LastTrackInLastSession:  lastTrack,
            DiscTypeCode:            discTypeCode,
            DiscIdValid:             didValid,
            DiscId:                  discId);
    }

    /// <summary>
    /// MMC READ TOC/PMA/ATIP (opcode 0x43) format 0 — the standard track listing.
    /// Returns first/last track numbers, lead-out position, and per-track LBA + type.
    /// Works on any CD with a readable TOC: audio CDs, data CDs, mixed-mode discs.
    /// </summary>
    public DiscToc ReadToc()
    {
        // Two-step read: first 4 bytes for the length header, then the full payload.
        var header = new byte[4];
        var cdb = new byte[10];
        cdb[0] = MmcOpcodes.ReadTocPmaAtip;
        cdb[1] = 0x00;     // LBA format (not MSF)
        cdb[2] = 0x00;     // Format 0 = standard TOC
        cdb[6] = 0x01;     // Starting track number
        cdb[7] = 0;        // allocation length high
        cdb[8] = 4;        // allocation length low — just the header
        SendScsi(cdb, cdbLength: 10, header, dataIn: true);

        // Bytes 0-1 = TOC data length (size of remaining payload, big-endian).
        int payloadLen = (header[0] << 8) | header[1];
        int totalLen = payloadLen + 2;  // include the two length bytes themselves

        var data = new byte[totalLen];
        cdb[7] = (byte)(totalLen >> 8);
        cdb[8] = (byte)(totalLen & 0xFF);
        SendScsi(cdb, cdbLength: 10, data, dataIn: true);

        int firstTrack = data[2];
        int lastTrack  = data[3];

        // Each entry is 8 bytes starting at offset 4.
        var tracks = new List<TocTrack>();
        int leadOutLba = 0;
        for (int i = 4; i + 7 < totalLen; i += 8)
        {
            byte control  = (byte)(data[i + 1] & 0x0F);
            byte trackNum = data[i + 2];
            int lba = (data[i + 4] << 24) | (data[i + 5] << 16)
                    | (data[i + 6] << 8)  |  data[i + 7];

            if (trackNum == 0xAA)
            {
                // Lead-out marker — gives us the total disc size.
                leadOutLba = lba;
            }
            else
            {
                // Control bits: bit 2 set = data track; bit 0 = pre-emphasis (audio only).
                bool isAudio = (control & 0x04) == 0;
                bool preemph = isAudio && (control & 0x01) != 0;
                tracks.Add(new TocTrack(trackNum, isAudio, preemph, lba, 0));
            }
        }

        // Track length = next-track-start-LBA minus this-track-start-LBA. Last
        // real track's length runs to the lead-out.
        for (int i = 0; i < tracks.Count; i++)
        {
            int nextLba = (i + 1 < tracks.Count) ? tracks[i + 1].StartLba : leadOutLba;
            tracks[i] = tracks[i] with { LengthLba = Math.Max(0, nextLba - tracks[i].StartLba) };
        }

        return new DiscToc(firstTrack, lastTrack, leadOutLba, tracks);
    }

    /// <summary>
    /// Send a SCSI command via IOCTL_SCSI_PASS_THROUGH_DIRECT.
    /// </summary>
    /// <param name="cdb">Command Descriptor Block (max 16 bytes used).</param>
    /// <param name="cdbLength">Actual CDB length (6, 10, 12, or 16).</param>
    /// <param name="dataBuffer">Buffer for DATA-IN reads or DATA-OUT writes (may be null/empty for unspecified).</param>
    /// <param name="dataIn">True = drive -> host, false = host -> drive (or unspecified if dataBuffer is empty).</param>
    /// <param name="timeoutSec">Command timeout in seconds.</param>
    public void SendScsi(byte[] cdb, byte cdbLength, byte[]? dataBuffer, bool dataIn, int timeoutSec = 30)
    {
        if (cdb.Length < cdbLength)
            throw new ArgumentException("CDB shorter than declared length");

        // SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER allocation. We use sequential
        // layout structs and a pinned buffer for DataBuffer.
        var spt = new SptiNative.ScsiPassThroughDirect
        {
            Length             = (ushort)Marshal.SizeOf<SptiNative.ScsiPassThroughDirect>(),
            ScsiStatus         = 0,
            PathId             = 0,
            TargetId           = 1,
            Lun                = 0,
            CdbLength          = cdbLength,
            SenseInfoLength    = 32,
            DataIn             = dataBuffer is null || dataBuffer.Length == 0
                                   ? SptiNative.SCSI_IOCTL_DATA_UNSPECIFIED
                                   : (dataIn ? SptiNative.SCSI_IOCTL_DATA_IN : SptiNative.SCSI_IOCTL_DATA_OUT),
            DataTransferLength = (uint)(dataBuffer?.Length ?? 0),
            TimeOutValue       = (uint)timeoutSec,
            DataBuffer         = IntPtr.Zero,    // set after pinning
            SenseInfoOffset    = 0,              // set below
            Cdb                = new byte[16],
        };
        Array.Copy(cdb, spt.Cdb, Math.Min(cdb.Length, 16));

        var wrapper = new SptiNative.ScsiPassThroughDirectWithBuffer
        {
            Spt      = spt,
            Filler   = 0,
            SenseBuf = new byte[32],
        };

        int wrapperSize = Marshal.SizeOf<SptiNative.ScsiPassThroughDirectWithBuffer>();
        wrapper.Spt.SenseInfoOffset = (uint)Marshal.OffsetOf<SptiNative.ScsiPassThroughDirectWithBuffer>(
            nameof(SptiNative.ScsiPassThroughDirectWithBuffer.SenseBuf));

        IntPtr wrapperPtr = Marshal.AllocHGlobal(wrapperSize);
        GCHandle dataHandle = default;
        try
        {
            if (dataBuffer is not null && dataBuffer.Length > 0)
            {
                dataHandle = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
                wrapper.Spt.DataBuffer = dataHandle.AddrOfPinnedObject();
            }

            Marshal.StructureToPtr(wrapper, wrapperPtr, fDeleteOld: false);

            bool ok = SptiNative.DeviceIoControl(
                _handle,
                SptiNative.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                wrapperPtr, (uint)wrapperSize,
                wrapperPtr, (uint)wrapperSize,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"DeviceIoControl(IOCTL_SCSI_PASS_THROUGH_DIRECT) failed (Win32 {err}) for opcode 0x{cdb[0]:X2}");
            }

            // Read back sense data + status.
            wrapper = Marshal.PtrToStructure<SptiNative.ScsiPassThroughDirectWithBuffer>(wrapperPtr);
            if (wrapper.Spt.ScsiStatus != 0)
            {
                // Sense key is the lower nibble of byte 2.
                byte senseKey = (byte)(wrapper.SenseBuf[2] & 0x0F);
                byte asc      = wrapper.SenseBuf[12];
                byte ascq     = wrapper.SenseBuf[13];
                throw new InvalidOperationException(
                    $"SCSI command 0x{cdb[0]:X2} returned status 0x{wrapper.Spt.ScsiStatus:X2} " +
                    $"(sense key 0x{senseKey:X1}, ASC 0x{asc:X2}, ASCQ 0x{ascq:X2})");
            }
        }
        finally
        {
            if (dataHandle.IsAllocated) dataHandle.Free();
            Marshal.FreeHGlobal(wrapperPtr);
        }
    }
}
