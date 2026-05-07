namespace Futureburn.Core.Image;

// A read-only stream that exposes the user-data portion of a BIN file as a
// plain ISO-style 2048-byte-per-sector byte stream. Handles both:
//   MODE1/2048 — bytes are already 2048 per sector; we just pass through
//   MODE1/2352 — each 2352-byte sector has [12B sync][4B header][2048B data][288B ECC],
//                we extract the 2048-byte payload and discard the rest.
//
// This lets SptiDataBurner consume a BIN file with the same pipeline as an ISO.
// For MODE1/2352 input the apparent stream length shrinks (2352 → 2048 per sector).

public sealed class BinCueImageStream : Stream
{
    private readonly Stream _bin;
    private readonly bool _isRaw2352;
    private readonly long _sourceSectors;
    private readonly long _logicalLength;   // exposed to consumer as 2048-per-sector data

    private long _logicalPosition;
    private byte[] _rawBuffer = Array.Empty<byte>();

    public BinCueImageStream(string binPath, CueTrackMode mode, int sectorBytes)
    {
        if (mode != CueTrackMode.Mode1)
            throw new NotSupportedException(
                $"Only MODE1 tracks are supported for BIN/CUE burning right now (got {mode}).");
        if (sectorBytes is not (2048 or 2352))
            throw new NotSupportedException(
                $"BIN sector size {sectorBytes} not supported. Use MODE1/2048 or MODE1/2352.");

        _bin = File.OpenRead(binPath);
        _isRaw2352 = sectorBytes == 2352;
        _sourceSectors = _bin.Length / sectorBytes;
        _logicalLength = _sourceSectors * 2048;
        if (_isRaw2352) _rawBuffer = new byte[2352];
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _logicalLength;
    public override long Position { get => _logicalPosition; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_isRaw2352)
        {
            int n = _bin.Read(buffer, offset, count);
            _logicalPosition += n;
            return n;
        }

        // Raw 2352 path: read whole sectors from the underlying stream and
        // emit only the 2048-byte payload portion.
        int totalEmitted = 0;
        while (count >= 2048)
        {
            int got = ReadFullSector();
            if (got < 2352) break;
            Buffer.BlockCopy(_rawBuffer, 16, buffer, offset, 2048);
            offset           += 2048;
            count            -= 2048;
            totalEmitted     += 2048;
            _logicalPosition += 2048;
        }
        return totalEmitted;
    }

    private int ReadFullSector()
    {
        int got = 0;
        while (got < 2352)
        {
            int n = _bin.Read(_rawBuffer, got, 2352 - got);
            if (n == 0) return got;
            got += n;
        }
        return got;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bin.Dispose();
        base.Dispose(disposing);
    }
}
