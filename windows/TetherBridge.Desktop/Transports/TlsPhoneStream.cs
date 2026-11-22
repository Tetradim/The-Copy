using System.Net.Security;

namespace TetherBridge.Desktop.Transports;

public sealed class TlsPhoneStream(SslStream tls) : Stream, IWriteHalfClose
{
    public Task ShutdownWriteAsync() => tls.ShutdownAsync();
    public override bool CanRead => tls.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => tls.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => tls.Flush();
    public override Task FlushAsync(CancellationToken token) => tls.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) => tls.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => tls.ReadAsync(buffer, token);
    public override void Write(byte[] buffer, int offset, int count) => tls.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => tls.WriteAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) tls.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await tls.DisposeAsync(); GC.SuppressFinalize(this); }
}
