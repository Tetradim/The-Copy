namespace TetherBridge.Desktop.FullTunnel;

public interface ITunnelRuntime
{
    Task PreflightAsync(CancellationToken token);
    Task StartEngineAsync(CancellationToken token);
    Task ConfigureAdapterAsync(CancellationToken token);
    Task StartRoutesAsync(CancellationToken token);
    Task RenewRoutesAsync(CancellationToken token);
    Task<bool> RemoveRoutesAsync(CancellationToken token);
    Task StopEngineAsync(CancellationToken token);
}

// Serializes lifecycle transitions and preserves the engine while route recovery is unresolved.
public sealed class TunnelSessionSupervisor(ITunnelRuntime runtime)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool engineAttempted, routesAttempted, active;

    public async Task StartAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (engineAttempted || routesAttempted || active) throw new InvalidOperationException("Session is active or recovery remains pending.");
            await runtime.PreflightAsync(token);
            try
            {
                engineAttempted = true;
                await runtime.StartEngineAsync(token);
                await runtime.ConfigureAdapterAsync(token);
                routesAttempted = true;
                await runtime.StartRoutesAsync(token);
                active = true;
            }
            catch (Exception failure)
            {
                await CleanupAfterFailureAsync(failure);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RenewAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!active) throw new InvalidOperationException("No active session.");
            try { await runtime.RenewRoutesAsync(token); }
            catch (Exception failure) { await CleanupAfterFailureAsync(failure); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await CleanupAsync(token); }
        finally { gate.Release(); }
    }

    private async Task CleanupAfterFailureAsync(Exception failure)
    {
        try { await CleanupAsync(CancellationToken.None); }
        catch (Exception cleanup) { throw new NetworkRecoveryPendingException("Tunnel cleanup remains pending.", new AggregateException(failure, cleanup)); }
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        active = false;
        if (routesAttempted)
        {
            if (!await runtime.RemoveRoutesAsync(token)) throw new IOException("Route removal remains unresolved.");
            routesAttempted = false;
        }
        if (engineAttempted)
        {
            await runtime.StopEngineAsync(token);
            engineAttempted = false;
        }
    }
}
