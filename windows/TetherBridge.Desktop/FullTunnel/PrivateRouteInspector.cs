using System.Net;
namespace TetherBridge.Desktop.FullTunnel;

public sealed record PrivateAdapterSnapshot(Guid AdapterId, ulong Luid, int Ipv4Index, int Ipv6Index, IReadOnlyList<WindowsRoute> Routes);

// The caller must establish that this adapter was freshly created by its own engine.
// Windows does not attach a transaction owner to an IP route.
public sealed class PrivateRouteInspector(Guid adapterId, ulong luid, Func<PrivateAdapterSnapshot?> snapshot,
    Func<IReadOnlyList<WindowsRoute>>? readRoutes = null) : IPrivateRouteInspector
{
    private readonly HashSet<(int Index, string Prefix, bool Ipv6)> prepared = new();
    private readonly object sync = new();
    public Task<PrivateRouteState> InspectAsync(int index, string prefix, bool ipv6, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var allowed = ipv6 ? TunnelRouteCommands.Ipv6Prefixes : TunnelRouteCommands.Ipv4Prefixes;
        if (!allowed.Contains(prefix, StringComparer.Ordinal)) return Task.FromResult(PrivateRouteState.Conflict);
        var current = snapshot();
        if (current is null && readRoutes is not null)
        {
            // Recheck identity after inventory so an adapter lookup race cannot authorize cleanup.
            var hasOwnedRoute = readRoutes().Any(route => route.InterfaceLuid == luid && route.Prefix == prefix);
            if (!hasOwnedRoute && snapshot() is null) return Task.FromResult(PrivateRouteState.AdapterGone);
        }
        if (current is null || current.AdapterId != adapterId || current.Luid != luid || (ipv6 ? current.Ipv6Index : current.Ipv4Index) != index)
            return Task.FromResult(PrivateRouteState.Conflict);
        var matching = current.Routes.Where(route => route.InterfaceIndex == index && route.Prefix == prefix).ToArray();
        if (matching.Length == 0) return Task.FromResult(PrivateRouteState.Absent);
        bool intended; lock (sync) intended = prepared.Contains((index, prefix, ipv6));
        var gateway = IPAddress.Parse(ipv6 ? "fd7a:6272:6964::1" : "198.18.0.1");
        var owned = intended && matching.Length == 1 && matching[0].InterfaceLuid == luid && matching[0].NextHop.Equals(gateway) &&
            matching[0].Metric == 1 && !matching[0].Immortal && matching[0].ValidLifetime is > 0 and <= 15;
        return Task.FromResult(owned ? PrivateRouteState.Owned : PrivateRouteState.Conflict);
    }
    public async Task PrepareAsync(int index, string prefix, bool ipv6, CancellationToken token)
    {
        if (await InspectAsync(index, prefix, ipv6, token) != PrivateRouteState.Absent) throw new IOException("Route was not absent on the owned adapter.");
        lock (sync) prepared.Add((index, prefix, ipv6));
    }
}
