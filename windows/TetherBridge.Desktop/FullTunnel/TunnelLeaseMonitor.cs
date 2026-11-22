using System.Diagnostics;

namespace TetherBridge.Desktop.FullTunnel;

// The health callback must be read-only. This monitor is not a substitute for
// recovery after process death; its caller owns the session lifetime.
public sealed class TunnelLeaseMonitor(TunnelSessionSupervisor supervisor, Func<CancellationToken, Task<bool>> health, TimeSpan interval)
{
    private int started;
    public async Task RunAsync(CancellationToken token)
    {
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(4)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Lease monitor may run only once.");
        Exception? failure = null;
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(interval, token);
                // A sleep or stalled scheduler may have allowed the 15-second route lease to expire.
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new IOException("Route lease renewal deadline was missed.");
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                healthTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                var probeToken = healthTimeout.Token;
                if (!await Task.Run(() => health(probeToken), CancellationToken.None).WaitAsync(probeToken)) throw new IOException("Tunnel health check failed.");
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new IOException("Route lease renewal deadline was missed.");
                await supervisor.RenewAsync(token);
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new IOException("Route lease renewal took too long.");
                clock.Restart();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        try { await supervisor.StopAsync(CancellationToken.None); }
        catch (Exception cleanup) { throw new NetworkRecoveryPendingException("Tunnel monitoring stopped but cleanup is incomplete.", failure is null ? cleanup : new AggregateException(failure, cleanup)); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
