namespace TetherBridge.Desktop.Transports;

// Shared per-direction pacing for all streams in a session.
public sealed class BandwidthLimiter(long bytesPerSecond)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly long rate = bytesPerSecond > 0 ? bytesPerSecond : throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
    public int MaxChunk => (int)Math.Min(32768, Math.Max(1, rate / 10));
    public async Task WaitAsync(int bytes, CancellationToken token)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0) return;
        var remaining = bytes;
        var quantum = (int)Math.Min(int.MaxValue, Math.Max(1, rate / 10));
        while (remaining > 0)
        {
            var count = Math.Min(remaining, quantum);
            await gate.WaitAsync(token);
            try { await Task.Delay(TimeSpan.FromSeconds(count / (double)rate), token); }
            finally { gate.Release(); }
            remaining -= count;
        }
    }
}
