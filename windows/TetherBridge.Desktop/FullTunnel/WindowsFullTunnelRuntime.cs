using System.Net;
using TetherBridge.Desktop.Services;

namespace TetherBridge.Desktop.FullTunnel;

// The caller must provide a dedicated loopback SOCKS relay backed by USB or RFCOMM.
public sealed class WindowsFullTunnelRuntime(IPAddress upstreamDns) : ITunnelRuntime
{
    private static readonly IPAddress[] RouteSamples =
        [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("208.67.222.222"),
         IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse("9000::1")];
    private readonly LocalDnsProxy dns = new();
    private TunnelEngine? engine;
    private PrivateAdapterSnapshot? identity;
    private RouteLeaseManager? routes;
    private readonly SemaphoreSlim sharingGate = new(1, 1);
    private readonly WindowsTunnelWifiShare sharing = new();
    private bool acceptingShare;

    public Task PreflightAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckCoverage();
        return Task.CompletedTask;
    }

    public async Task StartEngineAsync(CancellationToken token)
    {
        await StartDnsAsync(10810, token);
        engine = await TunnelEngineLauncher.StartAsync(token);
        identity = WindowsPrivateAdapterSnapshot.Read(engine.AdapterId)
            ?? throw new IOException("The tunnel adapter did not become available.");
    }

    // Opens only loopback listeners. The lab uses this to verify the exact DNS preflight
    // independently of the driver and the Windows system DNS configuration.
    public async Task StartDnsAsync(int socksPort, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var resolver = new SocksDnsResolver(socksPort, upstreamDns);
        dns.Start(resolver.ResolveAsync, 53, resolver.ResolveTcpAsync);
        try { await CheckDnsAsync(token); }
        catch { dns.Stop(); throw; }
    }

    public async Task ConfigureAdapterAsync(CancellationToken token)
    {
        var adapter = identity ?? throw new InvalidOperationException("No tunnel adapter.");
        var backend = new WindowsTunnelAdapterBackend(adapter.AdapterId, adapter.Luid,
            adapter.Ipv4Index, adapter.Ipv6Index, EngineAlive);
        var configurator = new TunnelAdapterConfigurator(adapter.AdapterId, adapter.Luid, backend.Read,
            backend.ReadOtherAddresses, backend.ReadOtherRoutes, () => dns.OwnsSystemDnsPorts, backend.ExecuteAsync);
        await configurator.ConfigureAsync(token);

        var inspector = new PrivateRouteInspector(adapter.AdapterId, adapter.Luid,
            () => WindowsPrivateAdapterSnapshot.Read(adapter.AdapterId), WindowsRouteTable.Read);
        routes = new RouteLeaseManager(new NetshRouteRunner(adapter.Ipv4Index, adapter.Ipv6Index, inspector));
    }

    public async Task StartRoutesAsync(CancellationToken token)
    {
        var adapter = identity ?? throw new InvalidOperationException("No tunnel adapter.");
        CheckCoverage();
        await CheckDnsAsync(token);
        await routes!.StartAsync(adapter.Ipv4Index, true, token, adapter.Ipv6Index);
        CheckPreferredRoutes(adapter, token);
        await sharingGate.WaitAsync(token);
        try { acceptingShare = true; }
        finally { sharingGate.Release(); }
    }

    public async Task RenewRoutesAsync(CancellationToken token)
    {
        CheckCoverage();
        await routes!.RenewAsync(token);
        var adapter = identity ?? throw new InvalidOperationException("No tunnel adapter.");
        CheckPreferredRoutes(adapter, token);
    }

    private static void CheckPreferredRoutes(PrivateAdapterSnapshot adapter, CancellationToken token)
    {
        foreach (var destination in RouteSamples)
        {
            token.ThrowIfCancellationRequested();
            var expected = destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? adapter.Ipv4Index : adapter.Ipv6Index;
            if (WindowsRouteTable.GetPreferredInterface(destination) != expected)
                throw new IOException($"Windows selected a different network interface for {destination}.");
        }
    }

    public async Task StartSharingAsync(CancellationToken token)
    {
        await sharingGate.WaitAsync(token);
        try
        {
            if (!acceptingShare || !EngineAlive() || identity is null)
                throw new InvalidOperationException("Wi-Fi Share requires an active tunnel.");
            await sharing.StartAsync(identity.AdapterId, token);
        }
        finally { sharingGate.Release(); }
    }

    public async Task<bool> RemoveRoutesAsync(CancellationToken token)
    {
        await sharingGate.WaitAsync(token);
        try
        {
            acceptingShare = false;
            try { await sharing.StopAsync(); }
            catch
            {
                if (routes is not null) await routes.StopAsync(token);
                throw;
            }
            return routes is null || await routes.StopAsync(token);
        }
        finally { sharingGate.Release(); }
    }

    public async Task StopEngineAsync(CancellationToken token)
    {
        if (engine is not null)
        {
            await engine.StopAsync(token);
            engine.Dispose();
            engine = null;
        }
        routes = null;
        identity = null;
        dns.Stop();
    }

    public async Task<bool> CheckHealthAsync(CancellationToken token)
    {
        if (!EngineAlive()) return false;
        await CheckDnsAsync(token);
        return EngineAlive();
    }

    private bool EngineAlive() => engine is not null && !engine.Process.HasExited;

    private void CheckCoverage()
    {
        var conflict = WindowsRouteTable.Read().FirstOrDefault(route =>
            route.InterfaceLuid != identity?.Luid && TunnelRouteCoverage.CanBypass(route.Prefix));
        if (conflict is not null)
            throw new IOException($"An existing route to {conflict.Prefix} could bypass the tunnel.");
    }

    private async Task CheckDnsAsync(CancellationToken token)
    {
        if (!dns.OwnsSystemDnsPorts) throw new IOException("The tunnel requires IPv4 and IPv6 DNS listeners on port 53.");
        await TunnelDnsProbe.CheckAsync(dns.Port, token);
    }
}
