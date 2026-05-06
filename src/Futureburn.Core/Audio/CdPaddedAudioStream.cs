using NAudio.Wave;

namespace Futureburn.Core.Audio;

// Wraps a CD-format WAV file as a Stream of raw PCM bytes, padded with
// silence (zero bytes) to a 2352-byte CD sector boundary.
//
// IMAPI2's AddAudioTrack requires:
//   - 44.1 kHz / 16-bit / stereo PCM
//   - data length must be a whole multiple of 2352 bytes (one CD frame)
//
// WaveFileReader hides the WAV header so Position/Length here are in terms
// of audio data only. Consumers see a continuous byte stream that ends on
// a CD sector boundary.
public sealed class CdPaddedAudioStream : Stream
{
    private readonly WaveFileReader _reader;
    private readonly long _audioBytes;   // actual audio data length (no padding)
    private readonly long _paddedBytes;  // rounded up to 2352-byte multiple
    private long _position;

    public CdPaddedAudioStream(string wavPath)
    {
        _reader = new WaveFileReader(wavPath);
        var fmt = _reader.WaveFormat;
        if (fmt.SampleRate    != CdFormat.SampleRate
         || fmt.Channels      != CdFormat.Channels
         || fmt.BitsPerSample != CdFormat.BitsPerSample
         || fmt.Encoding      != WaveFormatEncoding.Pcm)
        {
            _reader.Dispose();
            throw new InvalidOperationException(
                $"Track {wavPath} is not in CD format ({fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample}-bit, {fmt.Encoding}). " +
                "Decode it first.");
        }

        _audioBytes  = _reader.Length;
        _paddedBytes = ((_audioBytes + CdFormat.SectorBytes - 1) / CdFormat.SectorBytes) * CdFormat.SectorBytes;
    }

    public long PaddingBytes => _paddedBytes - _audioBytes;

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _paddedBytes;

    public override long Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, _paddedBytes);
            _reader.Position = Math.Min(_position, _audioBytes);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _paddedBytes - _position;
        if (remaining <= 0) return 0;

        int toRead = (int)Math.Min(count, remaining);
        int totalRead = 0;

        if (_position < _audioBytes)
        {
            long audioLeft = _audioBytes - _position;
            int audioToRead = (int)Math.Min(toRead, audioLeft);
            while (audioToRead > 0)
            {
                int n = _reader.Read(buffer, offset + totalRead, audioToRead);
                if (n == 0) break;
                totalRead   += n;
                audioToRead -= n;
                _position   += n;
            }
        }

        if (totalRead < toRead)
        {
            int padNeeded = toRead - totalRead;
            Array.Clear(buffer, offset + totalRead, padNeeded);
            totalRead += padNeeded;
            _position += padNeeded;
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _paddedBytes + offset,
            _                  => _position,
        };
        Position = newPos;
        return newPos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}
