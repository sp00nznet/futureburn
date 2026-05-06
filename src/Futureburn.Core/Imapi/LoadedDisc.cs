namespace Futureburn.Core.Imapi;

public sealed record LoadedDisc(
    Mmc.MediaPhysicalType MediaType,
    // True if MsftDiscFormat2Data could read capacity/speed info.
    // False for finalized discs, ROM media, audio CDs, and other non-data formats —
    // we still know the MediaType from the drive's profile, but the numeric fields
    // (TotalSectors, FreeSectors, etc.) will be zeros.
    bool HasFormatDetails,
    bool MediaPhysicallyBlank,
    bool MediaHeuristicallyBlank,
    long TotalSectors,
    long FreeSectors,
    long NextWritableAddress,
    int CurrentWriteSpeedKbps,
    IReadOnlyList<int> SupportedWriteSpeedsKbps)
{
    public string MediaTypeName => Mmc.MediaName(MediaType);

    // 2048-byte sectors is the standard data sector size for CD/DVD/BD.
    public long TotalBytes => TotalSectors * 2048L;
    public long FreeBytes  => FreeSectors  * 2048L;

    public bool IsBlank => MediaPhysicallyBlank || MediaHeuristicallyBlank;
}
