namespace TetherBridge.Desktop.FullTunnel;

public sealed class NetworkRecoveryPendingException(string message, Exception cause) : IOException(message, cause);
public enum PrivateRouteState { Absent, Owned, Conflict, AdapterGone }
public interface IPrivateRouteInspector
{
    // Must verify the adapter's stable GUID/LUID and exact route identity, not index alone.
    Task<PrivateRouteState> InspectAsync(int index, string prefix, bool ipv6, CancellationToken token);
    Task PrepareAsync(int index, string prefix, bool ipv6, CancellationToken token);
}
public interface IRouteCommandRunner : IPrivateRouteInspector { Task RunAsync(string[] args, CancellationToken token); }

// Only for an adapter freshly created and exclusively owned by this process.
public sealed class RouteLeaseManager(IRouteCommandRunner runner, TimeProvider? clock = null)
{
    private readonly List<(int Index, string Prefix, bool Ipv6)> attempted = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long? oldestLeaseStart;
    private void RequireFreshLease(long started)
    {
        if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
            throw new IOException("Private route lease is too old to safely maintain.");
    }
    public async Task StartAsync(int interfaceIndex, bool ipv6, CancellationToken token, int? ipv6InterfaceIndex = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (attempted.Count != 0) throw new InvalidOperationException("Route lease already active or recovery is pending.");
            if (interfaceIndex <= 0 || (ipv6 && (!ipv6InterfaceIndex.HasValue || ipv6InterfaceIndex.Value <= 0))) throw new ArgumentException("Valid interface indices are required.");
            try
            {
                foreach (var prefix in TunnelRouteCommands.Ipv4Prefixes) await AddAsync(interfaceIndex, prefix, false, token);
                if (ipv6) foreach (var prefix in TunnelRouteCommands.Ipv6Prefixes) await AddAsync(ipv6InterfaceIndex!.Value, prefix, true, token);
                await VerifyActiveRoutesAsync(token);
                RequireFreshLease(oldestLeaseStart!.Value);
            }
            catch (Exception failure)
            {
                if (!await StopCoreAsync(CancellationToken.None)) throw new NetworkRecoveryPendingException("Route activation failed and cleanup remains pending.", failure);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    private async Task AddAsync(int index, string prefix, bool ipv6, CancellationToken token)
    {
        if (await runner.InspectAsync(index, prefix, ipv6, token) != PrivateRouteState.Absent)
            throw new IOException("Route or adapter identity conflicts with existing state.");
        await runner.PrepareAsync(index, prefix, ipv6, token);
        oldestLeaseStart ??= time.GetTimestamp(); // earliest possible creation time, before the command
        RequireFreshLease(oldestLeaseStart.Value);
        attempted.Add((index, prefix, ipv6)); // record uncertain outcome before invoking netsh
        await runner.RunAsync(ipv6 ? TunnelRouteCommands.AddIpv6(index, prefix) : TunnelRouteCommands.AddIpv4(index, prefix), token);
    }
    public async Task RenewAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (attempted.Count == 0) throw new InvalidOperationException("No active route lease.");
            try
            {
                var previousStart = oldestLeaseStart ?? throw new IOException("Route lease timing is unavailable.");
                RequireFreshLease(previousStart);
                long? nextStart = null;
                foreach (var entry in attempted)
                {
                    RequireFreshLease(previousStart);
                    if (await runner.InspectAsync(entry.Index, entry.Prefix, entry.Ipv6, token) != PrivateRouteState.Owned) throw new IOException("Route ownership changed.");
                    nextStart ??= time.GetTimestamp();
                    await runner.RunAsync(entry.Ipv6 ? TunnelRouteCommands.RenewIpv6(entry.Index, entry.Prefix) : TunnelRouteCommands.RenewIpv4(entry.Index, entry.Prefix), token);
                    RequireFreshLease(previousStart);
                }
                await VerifyActiveRoutesAsync(token);
                RequireFreshLease(previousStart);
                RequireFreshLease(nextStart!.Value);
                oldestLeaseStart = nextStart;
            }
            catch (Exception failure)
            {
                if (!await StopCoreAsync(CancellationToken.None)) throw new NetworkRecoveryPendingException("Route renewal failed and cleanup remains pending.", failure);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    private async Task VerifyActiveRoutesAsync(CancellationToken token)
    {
        foreach (var route in attempted)
        {
            if (await runner.InspectAsync(route.Index, route.Prefix, route.Ipv6, token) != PrivateRouteState.Owned)
                throw new IOException($"Windows did not retain the expected private route to {route.Prefix}.");
        }
    }

    public async Task<bool> StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await StopCoreAsync(token); }
        finally { gate.Release(); }
    }
    private async Task<bool> StopCoreAsync(CancellationToken token)
    {
        for (var i = attempted.Count - 1; i >= 0; i--)
        {
            var entry = attempted[i];
            try
            {
                var state = await runner.InspectAsync(entry.Index, entry.Prefix, entry.Ipv6, token);
                if (state == PrivateRouteState.Owned)
                {
                    await runner.RunAsync(entry.Ipv6 ? TunnelRouteCommands.DeleteIpv6(entry.Index, entry.Prefix) : TunnelRouteCommands.DeleteIpv4(entry.Index, entry.Prefix), token);
                    state = await runner.InspectAsync(entry.Index, entry.Prefix, entry.Ipv6, token);
                }
                if (state is not (PrivateRouteState.Absent or PrivateRouteState.AdapterGone)) return false;
                attempted.RemoveAt(i);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return false; }
        }
        oldestLeaseStart = null;
        return true;
    }
}
