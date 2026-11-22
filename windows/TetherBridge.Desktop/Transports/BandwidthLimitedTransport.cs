using TetherBridge.Desktop.Services;
namespace TetherBridge.Desktop.Transports;

public sealed class BandwidthLimitedTransport(IPhoneTransport inner, long bytesPerSecond) : IPhoneTransport
{
    private readonly BandwidthLimiter up = new(bytesPerSecond);
    private readonly BandwidthLimiter down = new(bytesPerSecond);
    public string DisplayName => inner.DisplayName + " (bandwidth limited)";
    public async Task<Stream> ConnectAsync(CancellationToken token) => new LimitedStream(await inner.ConnectAsync(token), up, down);
    public Task<DiagnosticResult> ProbeHealthAsync(string? token) => inner.ProbeHealthAsync(token);
    public Task<DiagnosticResult> ProbeStatusAsync(string? token) => inner.ProbeStatusAsync(token);

    private sealed class LimitedStream(Stream innerStream, BandwidthLimiter up, BandwidthLimiter down) : Stream, IWriteHalfClose
    {
        public Task ShutdownWriteAsync() => innerStream is IWriteHalfClose half ? half.ShutdownWriteAsync() : throw new NotSupportedException("Transport does not support half-close.");
        public override bool CanRead => innerStream.CanRead; public override bool CanSeek => false; public override bool CanWrite => innerStream.CanWrite;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => innerStream.Flush(); public override Task FlushAsync(CancellationToken token) => innerStream.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await innerStream.ReadAsync(buffer[..Math.Min(buffer.Length, down.MaxChunk)], token);
            try { await down.WaitAsync(read, token); }
            catch (OperationCanceledException) { await innerStream.DisposeAsync(); throw; }
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, up.MaxChunk);
                await up.WaitAsync(count, token); await innerStream.WriteAsync(buffer[..count], token); buffer = buffer[count..];
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) innerStream.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await innerStream.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
