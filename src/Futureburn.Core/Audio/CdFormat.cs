namespace Futureburn.Core.Audio;

// Audio CD format constants. Red Book audio is fixed at 44.1 kHz / 16-bit /
// stereo signed PCM, big or little endian depending on who you ask. The
// physical disc layout uses 2352-byte sectors at 75 sectors/sec (CD frames).
public static class CdFormat
{
    public const int SampleRate     = 44100;
    public const int BitsPerSample  = 16;
    public const int Channels       = 2;
    public const int BytesPerSample = BitsPerSample / 8;
    public const int BlockAlign     = Channels * BytesPerSample;          // 4 bytes per stereo frame
    public const int BytesPerSecond = SampleRate * BlockAlign;            // 176,400

    // CD audio sector layout — 2352 bytes per sector, 75 sectors per second.
    public const int SectorBytes      = 2352;
    public const int SectorsPerSecond = 75;
    public const int SamplesPerSector = SectorBytes / BlockAlign;         // 588 stereo frames

    // Common disc capacities, expressed in sectors (frames).
    public const int Sectors74Min = 74 * 60 * SectorsPerSecond;           // 333,000
    public const int Sectors80Min = 80 * 60 * SectorsPerSecond;           // 360,000

    public static long BytesForDuration(TimeSpan duration)
        => (long)Math.Ceiling(duration.TotalSeconds * BytesPerSecond);

    public static long SectorsForDuration(TimeSpan duration)
        => (long)Math.Ceiling(duration.TotalSeconds * SectorsPerSecond);
}
