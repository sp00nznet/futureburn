namespace Futureburn.Core.Spti;

// SCSI MMC (Multi-Media Command) opcodes we care about for optical drive work.
// Sourced from MMC-6 spec (T10/1836-D).
//
// Each command is identified by its opcode byte (the first byte of the CDB).
// The full Command Descriptor Block (CDB) is constructed by SptiDevice helpers.

public static class MmcOpcodes
{
    public const byte TestUnitReady       = 0x00;
    public const byte Inquiry             = 0x12;
    public const byte ModeSense6          = 0x1A;
    public const byte StartStopUnit       = 0x1B;
    public const byte PreventAllowMedium  = 0x1E;
    public const byte ReadCapacity10      = 0x25;
    public const byte Read10              = 0x28;
    public const byte Write10             = 0x2A;
    public const byte SeekTrackInformation = 0x47;
    public const byte ReadTocPmaAtip      = 0x43;
    public const byte ReadDiscInformation = 0x51;
    public const byte ReadTrackInformation = 0x52;
    public const byte ReserveTrack        = 0x53;
    public const byte ModeSelect10        = 0x55;
    public const byte ReadBufferCapacity  = 0x5C;
    public const byte ModeSense10         = 0x5A;
    public const byte SendCueSheet        = 0x5D;
    public const byte CloseTrackSession   = 0x5B;
    public const byte BlankCommand        = 0xA1;   // CD-RW erase
    public const byte SendKey             = 0xA3;
    public const byte ReportKey           = 0xA4;
    public const byte LoadUnload          = 0xA6;
    public const byte SetCdSpeed          = 0xBB;
    public const byte ReadCd              = 0xBE;
    public const byte ReadDiscStructure   = 0xAD;
    public const byte SetStreaming        = 0xB6;
    public const byte Write12             = 0xAA;
    public const byte SynchronizeCache    = 0x35;
}
