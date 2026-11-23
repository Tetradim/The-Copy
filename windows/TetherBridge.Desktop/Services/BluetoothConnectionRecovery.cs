namespace TetherBridge.Desktop.Services;

internal static class BluetoothConnectionRecovery
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    public static async Task<DiagnosticResult> RetryAsync(
        Func<CancellationToken, Task<DiagnosticResult>> probe, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        wait ??= Task.Delay;
        DiagnosticResult? latest = null;
        foreach (var delay in RetryDelays)
        {
            await wait(delay, token);
            token.ThrowIfCancellationRequested();
            latest = await probe(token);
            token.ThrowIfCancellationRequested();
            if (latest.Ok) return latest;
        }
        return latest!;
    }

    public static async Task<Stream> OpenWithDeadlineAsync(Func<CancellationToken, Task<Stream>> open,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { return await open(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The Bluetooth data connection did not open in time.");
        }
    }
}
