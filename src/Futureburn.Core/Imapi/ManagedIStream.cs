using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComStat = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Futureburn.Core.Imapi;

// Adapts a .NET Stream to a COM IStream so we can pass it to IMAPI2's
// AddAudioTrack. IMAPI seeks the stream to read its size before reading
// the data, so the underlying stream must support seeking and Length.
//
// Marked [ComVisible(true)] so .NET creates a COM Callable Wrapper that
// IMAPI can talk to even when the assembly default is ComVisible(false).
[ComVisible(true)]
public sealed class ManagedIStream : IStream, IDisposable
{
    private readonly Stream _stream;

    public ManagedIStream(Stream stream) => _stream = stream;

    public void Dispose() => _stream.Dispose();

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int total = 0;
        while (total < cb)
        {
            int n = _stream.Read(pv, total, cb - total);
            if (n == 0) break;
            total += n;
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        _stream.Write(pv, 0, cb);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long pos = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void SetSize(long libNewSize) => _stream.SetLength(libNewSize);

    public void Stat(out ComStat pstatstg, int grfStatFlag)
    {
        pstatstg = new ComStat
        {
            cbSize = _stream.Length,
            type   = 2, // STGTY_STREAM
        };
    }

    // Operations IMAPI doesn't need; if anything calls them, we want to know.
    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        => throw new NotSupportedException();
    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType)
        => throw new NotSupportedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType)
        => throw new NotSupportedException();
    public void Clone(out IStream ppstm)
        => throw new NotSupportedException();
}
