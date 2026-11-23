using System.Buffers.Binary;

namespace TetherBridge.Desktop.Transports;

// One TCP stream per request. Each record is a big-endian 32-bit length followed
// by that many bytes; a zero length signals an orderly half-close in that direction.
public sealed class FramedUsbStream(Stream inner, Action? release = null) : Stream, IWriteHalfClose
{
    private const int MaxFrame = 32768;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _remaining;
    private int _disposed;
    private bool _readEnded;
    private bool _writeEnded;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite && !_writeEnded;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.IsEmpty) return 0;
        await _readLock.WaitAsync(token);
        try
        {
            if (_readEnded) return 0;
            if (_remaining == 0)
            {
                var header = new byte[4];
                await inner.ReadExactlyAsync(header, token);
                var size = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (size == 0) { _readEnded = true; return 0; }
                if (size > MaxFrame) throw new IOException("Invalid USB frame length.");
                _remaining = (int)size;
            }
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], token);
            if (count == 0) throw new EndOfStreamException("USB frame was truncated.");
            _remaining -= count;
            return count;
        }
        finally { _readLock.Release(); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            if (_writeEnded) throw new IOException("USB write side is closed.");
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, MaxFrame);
                var header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)count);
                await inner.WriteAsync(header, token);
                await inner.WriteAsync(buffer[..count], token);
                buffer = buffer[count..];
            }
        }
        finally { _writeLock.Release(); }
    }
    public async Task ShutdownWriteAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_writeEnded) return;
            await inner.WriteAsync(new byte[4]);
            await inner.FlushAsync();
            _writeEnded = true;
        }
        finally { _writeLock.Release(); }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { inner.Dispose(); }
            finally { release?.Invoke(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { await inner.DisposeAsync(); }
            finally { release?.Invoke(); }
        }
        GC.SuppressFinalize(this);
    }
}
