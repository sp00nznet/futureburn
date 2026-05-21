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
    /// MMC START STOP UNIT (0x1B) — eject or load the disc tray. The drive must
    /// be willing to eject (some drives ignore the command if media is mounted
    /// by Windows; usually fine for blank discs).
    /// </summary>
    public void EjectTray()  => StartStopUnit(start: false, loadEject: true);
    public void LoadTray()   => StartStopUnit(start: true,  loadEject: true);

    /// <summary>
    /// MMC TEST UNIT READY (0x00) — zero-byte command that asks the drive
    /// to confirm it can accept commands. Returns success if ready;
    /// throws an <see cref="SptiScsiException"/> with a meaningful sense
    /// key otherwise (most commonly NOT_READY 0x2 or UNIT_ATTENTION 0x6).
    /// </summary>
    /// <summary>
    /// MMC RESERVE TRACK (0x53). Pre-allocate space for the next track in
    /// TAO multi-track recording. Tells the drive "the next WRITE 12 stream
    /// will be exactly <paramref name="sizeInSectors"/> blocks long," so it
    /// can set up its track-boundary state machine before any data arrives.
    /// <para>
    /// This is the missing ingredient we discovered after multiple
    /// multi-track burn failures on the GE20LU10 — without RESERVE TRACK,
    /// the drive accepts writes but never establishes a clean track-2
    /// boundary, eventually raising UNIT ATTENTION mid-track. cdrecord and
    /// every working CD writer issue this before each track.
    /// </para>
    /// </summary>
    public void ReserveTrack(int sizeInSectors)
    {
        var cdb = new byte[16];
        cdb[0] = 0x53;          // RESERVE TRACK opcode
        cdb[1] = 0x00;          // ARSV=0, RMV=0 — fresh fixed-size reservation
        cdb[5] = (byte)((sizeInSectors >> 24) & 0xFF);
        cdb[6] = (byte)((sizeInSectors >> 16) & 0xFF);
        cdb[7] = (byte)((sizeInSectors >>  8) & 0xFF);
        cdb[8] = (byte)( sizeInSectors        & 0xFF);
        SendScsi(cdb, cdbLength: 10, dataBuffer: null, dataIn: false, timeoutSec: 30);
    }

    public void TestUnitReady()
    {
        var cdb = new byte[16];   // opcode 0x00 + zeros
        SendScsi(cdb, cdbLength: 6, dataBuffer: null, dataIn: false, timeoutSec: 10);
    }

    /// <summary>
    /// Poll TEST UNIT READY until the drive becomes ready, absorbing the
    /// transient sense conditions raised right after a state-changing
    /// command (CLOSE TRACK, MODE SELECT, etc.):
    ///   - 0x6 UNIT ATTENTION: a state change just happened. The TUR
    ///     itself "consumes" the UA — the very next command should pass.
    ///   - 0x2 NOT READY: drive is still doing internal work
    ///     (writing the gap, finalizing track metadata). Wait briefly.
    /// Anything else surfaces unchanged.
    /// </summary>
    public void WaitUntilReady(int timeoutSec = 60, Action<string>? onLog = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                TestUnitReady();
                if (attempts > 1)
                    onLog?.Invoke($"     drive ready after {attempts} TUR polls");
                return;
            }
            catch (SptiScsiException ex) when (ex.SenseKey == 0x6)
            {
                // UNIT ATTENTION absorbed by this TUR. Loop immediately.
                continue;
            }
            catch (SptiScsiException ex) when (ex.SenseKey == 0x2)
            {
                // NOT READY (drive still working). Brief wait, then retry.
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException(
                        $"Drive still NOT READY after {timeoutSec}s polling " +
                        $"(last sense: {ex.Message}).");
                Thread.Sleep(200);
                continue;
            }
        }
    }

    private void StartStopUnit(bool start, bool loadEject)
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.StartStopUnit;
        cdb[1] = 0x00;  // IMMED = 0 (block until done)
        // CDB byte 4 bits: START (bit 0), LOEJ (bit 1)
        byte b4 = 0;
        if (start)     b4 |= 0x01;
        if (loadEject) b4 |= 0x02;
        cdb[4] = b4;
        SendScsi(cdb, cdbLength: 6, dataBuffer: null, dataIn: false, timeoutSec: 60);
    }

    /// <summary>
    /// MMC SET CD SPEED (0xBB). Speeds in KB/s. Pass 0xFFFF to mean "max".
    /// Audio CD 1x is 176 KB/s (raw rate), so 16x ≈ 2,816 KB/s, 48x ≈ 8,448 KB/s.
    /// </summary>
    public void SetCdSpeed(int readSpeedKbps, int writeSpeedKbps)
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.SetCdSpeed;
        cdb[2] = (byte)(readSpeedKbps  >> 8);
        cdb[3] = (byte)(readSpeedKbps  & 0xFF);
        cdb[4] = (byte)(writeSpeedKbps >> 8);
        cdb[5] = (byte)(writeSpeedKbps & 0xFF);
        SendScsi(cdb, cdbLength: 12, dataBuffer: null, dataIn: false);
    }

    /// <summary>
    /// MMC MODE SELECT 10 (0x55) — write a SCSI mode page. We use this to
    /// configure the CD Write Parameters Mode Page (0x05), which tells the drive
    /// to do TAO writes of CD-DA audio sectors at 2352 bytes raw.
    /// </summary>
    public void ModeSelect10(byte[] parameterList)
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.ModeSelect10;
        cdb[1] = 0x10;  // PF = 1 (page format), SP = 0 (don't save to flash)
        cdb[7] = (byte)(parameterList.Length >> 8);
        cdb[8] = (byte)(parameterList.Length & 0xFF);
        SendScsi(cdb, cdbLength: 10, dataBuffer: parameterList, dataIn: false);
    }

    public enum CdAudioWriteMode : byte { TrackAtOnce = 1, SessionAtOnce = 2 }

    /// <summary>
    /// Configure the drive for writing data sectors (Mode 1, 2048 bytes per
    /// sector) to a blank CD-R/CD-RW. Uses TAO single-data-track layout —
    /// the standard for ISO 9660 / Joliet data CDs.
    /// </summary>
    public void ConfigureForDataCd()
    {
        var p = new byte[8 + 52];
        int o = 8;
        p[o + 0]  = 0x05;       // Page Code = 5 (CD Write Parameters)
        p[o + 1]  = 0x32;       // Page Length = 50
        p[o + 2]  = 0x41;       // BUFE (bit 6) + WriteType = 1 (TAO)
        p[o + 3]  = 0xC4;       // Multisession = 11 (final session) + TrackMode = 4 (Mode 1 data, recorded uninterrupted)
        p[o + 4]  = 0x08;       // DataBlockType = 8 (Mode 1, 2048 bytes per sector)
        p[o + 6]  = 0x20;       // Initiator App Code
        p[o + 7]  = 0x00;       // Session Format = 0 (CD-ROM)
        ModeSelect10(p);
    }

    /// <summary>
    /// Configure the drive for writing data sectors to a blank DVD-R/RW/+R/+RW.
    /// Uses SAO mode (Session-At-Once / Disc-At-Once) which is the standard for
    /// single-image DVD writes. Most DVD recorders only do SAO/DAO for sequential
    /// recording.
    /// </summary>
    public void ConfigureForDataDvd()
    {
        var p = new byte[8 + 52];
        int o = 8;
        p[o + 0]  = 0x05;       // Page Code = 5
        p[o + 1]  = 0x32;       // Page Length = 50
        p[o + 2]  = 0x42;       // BUFE (bit 6) + WriteType = 2 (SAO/DAO)
        p[o + 3]  = 0xC0;       // Multisession = 11 (final session); TrackMode is mostly ignored on DVD
        p[o + 4]  = 0x08;       // DataBlockType = 8 (Mode 1, 2048 bytes per sector)
        p[o + 6]  = 0x20;       // App code
        p[o + 7]  = 0x00;       // Session Format = 0
        ModeSelect10(p);
    }

    /// <summary>
    /// Configure the drive for CD-DA TAO writing. After this call, WRITE 12
    /// commands at the current writable address will go straight onto the disc
    /// as raw 2352-byte audio sectors.
    /// </summary>
    public void ConfigureForAudioTao() => ConfigureForAudio(CdAudioWriteMode.TrackAtOnce);

    /// <summary>
    /// Configure the drive for CD-DA writing in the chosen mode. SessionAtOnce
    /// is required for gapless burning via SEND CUE SHEET.
    /// </summary>
    public void ConfigureForAudio(CdAudioWriteMode mode)
    {
        // Mode Parameter Header (8 bytes): all zeros for our purposes.
        // Mode Page 0x05 (CD Write Parameters), 52-byte page (page header 2 + 50).
        var p = new byte[8 + 52];

        // Mode header — 8 bytes of zeros is fine for SELECT.
        // (Drive ignores most of these on input.)

        // Page 0x05 starts at offset 8.
        int o = 8;
        p[o + 0]  = 0x05;       // Page Code = 5 (CD Write Parameters)
        p[o + 1]  = 0x32;       // Page Length = 50
        p[o + 2]  = (byte)(0x40 | (byte)mode);  // BUFE (bit 6) + WriteType (1=TAO, 2=SAO/DAO)
                                // BUFE = Buffer Underrun-Free Enabled (BURN-Proof).
                                // Without this, a momentary buffer underrun trashes
                                // the disc with a medium ECC error mid-write. Every
                                // modern burning tool enables this; it was an
                                // embarrassing oversight in our earlier attempts.
        p[o + 3]  = 0xC0;       // Multisession = 11 (final session), FP=0, Copy=0, TrackMode=0 (Audio CD-DA)
        p[o + 4]  = 0x00;       // DataBlockType = 0 (raw 2352-byte sectors for CD-DA)
        p[o + 5]  = 0x00;       // LinkSize
        p[o + 6]  = 0x20;       // Initiator App Code = 0x20 (writer app)
        p[o + 7]  = 0x00;       // Session Format = 0 (CD-DA / CD-ROM)
        // Bytes 8-9: PacketSize (we don't use packet writing)
        // Bytes 10-11: Audio Pause Length = 150 sectors = 2 seconds (the standard Red Book gap).
        p[o + 10] = 0x00;
        p[o + 11] = 0x96;       // 150
        // Bytes 12-50: MCN, ISRC, sub-header — leave zero (no media catalog or ISRC).

        ModeSelect10(p);
    }

    /// <summary>
    /// MMC SEND CUE SHEET (0x5D). Hands the drive the disc layout for SAO/DAO
    /// burning. Must be called after MODE SELECT 10 with WriteType = SAO and
    /// before the first WRITE 12.
    /// </summary>
    public void SendCueSheet(byte[] cueSheet)
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.SendCueSheet;
        cdb[6] = (byte)((cueSheet.Length >> 16) & 0xFF);
        cdb[7] = (byte)((cueSheet.Length >>  8) & 0xFF);
        cdb[8] = (byte)( cueSheet.Length        & 0xFF);
        SendScsi(cdb, cdbLength: 10, dataBuffer: cueSheet, dataIn: false, timeoutSec: 60);
    }

    /// <summary>
    /// MMC WRITE 12 (0xAA). Writes <paramref name="numBlocks"/> sectors of
    /// <paramref name="data"/> starting at LBA <paramref name="startLba"/>.
    /// For CD-DA audio TAO, blocks are 2352 bytes each.
    /// </summary>
    public void Write12(int startLba, int numBlocks, byte[] data, int timeoutSec = 60, int? dataLength = null)
    {
        // Per the SPTI doc, the kernel-visible byte count MUST agree with what
        // the CDB describes (numBlocks × sectorSize). If the caller hands us a
        // fixed-size scratch buffer larger than the actual transfer, they must
        // also tell us the real byte count via dataLength — otherwise the
        // mismatch will trigger STATUS_INVALID_PARAMETER or a USB-BOT reset.
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.Write12;
        cdb[2] = (byte)((startLba >> 24) & 0xFF);
        cdb[3] = (byte)((startLba >> 16) & 0xFF);
        cdb[4] = (byte)((startLba >>  8) & 0xFF);
        cdb[5] = (byte)( startLba        & 0xFF);
        cdb[6] = (byte)((numBlocks >> 24) & 0xFF);
        cdb[7] = (byte)((numBlocks >> 16) & 0xFF);
        cdb[8] = (byte)((numBlocks >>  8) & 0xFF);
        cdb[9] = (byte)( numBlocks        & 0xFF);
        SendScsi(cdb, cdbLength: 12, data, dataIn: false,
                 timeoutSec: timeoutSec, dataLength: dataLength);
    }

    /// <summary>
    /// MMC SYNCHRONIZE CACHE (0x35). Tells the drive to flush any internally
    /// buffered data to the disc. Call after each track's WRITE 12 loop to
    /// ensure the data is committed before CLOSE TRACK.
    /// </summary>
    public void SynchronizeCache()
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.SynchronizeCache;
        SendScsi(cdb, cdbLength: 10, dataBuffer: null, dataIn: false, timeoutSec: 120);
    }

    /// <summary>
    /// MMC CLOSE TRACK / SESSION / DISC (0x5B). Function:
    ///   1 = close the specified track
    ///   2 = close the current session (finalizes the disc for the session)
    ///   6 = close the entire disc (disc-at-once finalize)
    /// <para>
    /// On <paramref name="immediate"/>=true the drive returns as soon as the
    /// command is accepted and finishes asynchronously. The caller must poll
    /// <see cref="WaitUntilReady"/> + observable side effects (e.g. inter-track
    /// gap appearing in NextWritableLba) before assuming completion. The LG
    /// GE20LU10 silently treats IMMED=0 as IMMED=1 anyway, so the explicit
    /// async pattern is more honest.
    /// </para>
    /// </summary>
    public void CloseTrackOrSession(byte function, int trackNumber, int timeoutSec = 240, bool immediate = false)
    {
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.CloseTrackSession;
        cdb[1] = immediate ? (byte)0x01 : (byte)0x00;
        cdb[2] = function;
        cdb[4] = (byte)((trackNumber >> 8) & 0xFF);
        cdb[5] = (byte)( trackNumber       & 0xFF);
        SendScsi(cdb, cdbLength: 10, dataBuffer: null, dataIn: false, timeoutSec: timeoutSec);
    }

    public sealed record TrackInformation(
        int TrackNumber,
        int SessionNumber,
        bool IsAudioTrack,
        int TrackStartLba,
        int NextWritableLba,
        int FreeBlocks,
        int TrackSize,
        bool ReservedTrack,
        bool Damaged);

    /// <summary>
    /// MMC READ TRACK INFORMATION (0x52) — get state of a single track. We
    /// pass `trackNumber = 0xFF` to mean "the invisible/incomplete track" which
    /// for a blank disc is track 1's starting position.
    /// </summary>
    public TrackInformation ReadTrackInformation(int trackNumber)
    {
        var data = new byte[36];
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.ReadTrackInformation;
        cdb[1] = 0x01;  // address/number type: 01 = LTN (Logical Track Number)
        cdb[2] = (byte)((trackNumber >> 24) & 0xFF);
        cdb[3] = (byte)((trackNumber >> 16) & 0xFF);
        cdb[4] = (byte)((trackNumber >>  8) & 0xFF);
        cdb[5] = (byte)( trackNumber        & 0xFF);
        cdb[7] = (byte)(data.Length >> 8);
        cdb[8] = (byte)(data.Length & 0xFF);
        SendScsi(cdb, cdbLength: 10, data, dataIn: true);

        int dataLen = (data[0] << 8) | data[1];
        int trackNum = data[2];           // (lower 8 bits; full number includes byte 32)
        int sessionNum = data[3];
        int trackStart = (data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11];
        int nextWritable = (data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15];
        int freeBlocks = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int trackSize = (data[24] << 24) | (data[25] << 16) | (data[26] << 8) | data[27];
        bool reserved = (data[6] & 0x80) != 0;
        bool damaged  = (data[5] & 0x20) != 0;
        // Track Mode = bits 3-0 of byte 5; if 0 = audio (CD-DA), if 4 = data
        bool isAudio = (data[5] & 0x04) == 0;

        return new TrackInformation(
            TrackNumber:     trackNum,
            SessionNumber:   sessionNum,
            IsAudioTrack:    isAudio,
            TrackStartLba:   trackStart,
            NextWritableLba: nextWritable,
            FreeBlocks:      freeBlocks,
            TrackSize:       trackSize,
            ReservedTrack:   reserved,
            Damaged:         damaged);
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
    /// MMC READ TOC/PMA/ATIP (opcode 0x43) format 0100b — the ATIP (Absolute
    /// Time In Pre-groove) of a recordable disc. We use it for one field: the
    /// start address of the lead-in, which is where CD-Text must be written.
    /// <para>
    /// The lead-in start is returned as an MSF that wraps near 99 minutes to
    /// represent a negative LBA — e.g. 97:26:65 → LBA -11635. The program area
    /// begins at LBA 0 (= MSF 00:02:00); the lead-in occupies the negative LBAs
    /// from this value up to LBA -150.
    /// </para>
    /// </summary>
    public int ReadAtipLeadInStartLba()
    {
        var data = new byte[32];
        var cdb = new byte[10];
        cdb[0] = MmcOpcodes.ReadTocPmaAtip;
        cdb[2] = 0x04;     // Format 0100b = ATIP
        cdb[7] = (byte)(data.Length >> 8);
        cdb[8] = (byte)(data.Length & 0xFF);
        SendScsi(cdb, cdbLength: 10, data, dataIn: true);

        // ATIP descriptor: bytes 8-10 = start time of lead-in (Min, Sec, Frame).
        int m = data[8], s = data[9], f = data[10];
        int lba = (m * 60 + s) * 75 + f - 150;
        // Lead-in MSF minutes are in the 90s — that's the negative-LBA wrap.
        if (m >= 90) lba -= 100 * 60 * 75;
        return lba;
    }

    /// <summary>
    /// MMC WRITE 10 (0x2A) used for the CD-Text lead-in. Blocks here are 96-byte
    /// R-W subchannel sectors (four 24-bit-expanded CD-Text packs), and
    /// <paramref name="startLba"/> is negative (a lead-in address). The drive
    /// knows these blocks are 96-byte CD-Text — not 2352-byte audio — because
    /// the cue sheet's lead-in entries carry DATA FORM 0x41.
    /// </summary>
    public void WriteCdTextLeadIn(int startLba, int numBlocks, byte[] data, int timeoutSec = 60)
    {
        const int blockBytes = 96;
        var cdb = new byte[16];
        cdb[0] = MmcOpcodes.Write10;
        // LBA is signed: a negative lead-in address packs as two's complement.
        cdb[2] = (byte)((startLba >> 24) & 0xFF);
        cdb[3] = (byte)((startLba >> 16) & 0xFF);
        cdb[4] = (byte)((startLba >>  8) & 0xFF);
        cdb[5] = (byte)( startLba        & 0xFF);
        cdb[7] = (byte)((numBlocks >> 8) & 0xFF);
        cdb[8] = (byte)( numBlocks       & 0xFF);
        SendScsi(cdb, cdbLength: 10, data, dataIn: false,
                 timeoutSec: timeoutSec, dataLength: numBlocks * blockBytes);
    }

    /// <summary>
    /// Send a SCSI command via IOCTL_SCSI_PASS_THROUGH_DIRECT.
    /// </summary>
    /// <param name="cdb">Command Descriptor Block (max 16 bytes used).</param>
    /// <param name="cdbLength">Actual CDB length (6, 10, 12, or 16).</param>
    /// <param name="dataBuffer">Buffer for DATA-IN reads or DATA-OUT writes (may be null/empty for unspecified).</param>
    /// <param name="dataIn">True = drive -> host, false = host -> drive (or unspecified if dataBuffer is empty).</param>
    /// <param name="timeoutSec">Command timeout in seconds.</param>
    /// <param name="dataLength">
    /// Explicit transfer length in bytes. When null, defaults to dataBuffer.Length.
    /// Set this when the buffer is larger than the actual transfer (e.g. a fixed-size
    /// scratch buffer being used for a short read or write). Microsoft's SPTI doc
    /// requires DataTransferLength to be the device-described byte count — passing
    /// a too-large value can cause STATUS_INVALID_PARAMETER or, on USB-BOT optical
    /// drives, a transport reset that surfaces as sense 0x6/0x29/0x00 on the next
    /// command. Cf. cdrtools and libburn which keep CDB-blocks and data-bytes
    /// locked together via a single buffer struct.
    /// </param>
    public void SendScsi(byte[] cdb, byte cdbLength, byte[]? dataBuffer, bool dataIn,
                         int timeoutSec = 30, int? dataLength = null)
    {
        if (cdb.Length < cdbLength)
            throw new ArgumentException("CDB shorter than declared length");

        int transferLen = dataLength ?? (dataBuffer?.Length ?? 0);
        if (transferLen < 0 || (dataBuffer is not null && transferLen > dataBuffer.Length))
            throw new ArgumentException(
                $"dataLength {transferLen} can't be negative or exceed buffer length {dataBuffer?.Length ?? 0}");

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
            DataIn             = dataBuffer is null || transferLen == 0
                                   ? SptiNative.SCSI_IOCTL_DATA_UNSPECIFIED
                                   : (dataIn ? SptiNative.SCSI_IOCTL_DATA_IN : SptiNative.SCSI_IOCTL_DATA_OUT),
            DataTransferLength = (uint)transferLen,
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
                throw new SptiScsiException(cdb[0], wrapper.Spt.ScsiStatus, senseKey, asc, ascq);
            }
        }
        finally
        {
            if (dataHandle.IsAllocated) dataHandle.Free();
            Marshal.FreeHGlobal(wrapperPtr);
        }
    }
}

/// <summary>
/// Thrown by <see cref="SptiDevice.SendScsi"/> when a SCSI command returns
/// a non-zero status. Carries the structured sense triple so callers can do
/// typed retry decisions (e.g. retry on UNIT ATTENTION 0x6) instead of
/// parsing the message string.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SptiScsiException : InvalidOperationException
{
    public byte Opcode     { get; }
    public byte ScsiStatus { get; }
    public byte SenseKey   { get; }
    public byte Asc        { get; }
    public byte Ascq       { get; }

    public SptiScsiException(byte opcode, byte scsiStatus, byte senseKey, byte asc, byte ascq)
        : base($"SCSI command 0x{opcode:X2} returned status 0x{scsiStatus:X2} " +
               $"(sense key 0x{senseKey:X1}, ASC 0x{asc:X2}, ASCQ 0x{ascq:X2})")
    {
        Opcode     = opcode;
        ScsiStatus = scsiStatus;
        SenseKey   = senseKey;
        Asc        = asc;
        Ascq       = ascq;
    }
}
