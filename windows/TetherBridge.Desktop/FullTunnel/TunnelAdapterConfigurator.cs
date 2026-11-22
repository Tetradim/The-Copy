using System.Net;
using System.Net.Sockets;
namespace TetherBridge.Desktop.FullTunnel;

public sealed record TunnelAdapterState(Guid AdapterId, ulong Luid, int Ipv4Index, int Ipv6Index,
    IReadOnlyCollection<IPAddress> Addresses, IReadOnlyCollection<IPAddress> DnsServers);

// The snapshot and command providers must refer exclusively to a freshly created adapter.
// On any failure the owning engine must be stopped and the adapter removed.
public sealed class TunnelAdapterConfigurator(Guid adapterId, ulong luid, Func<TunnelAdapterState?> snapshot,
    Func<IReadOnlyCollection<IPAddress>> otherAddresses, Func<IReadOnlyCollection<WindowsRoute>> otherRoutes, Func<bool> ownsDnsListener,
    Func<string[], CancellationToken, Task> execute)
{
    public async Task ConfigureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ownsDnsListener()) throw new IOException("TetherBridge does not own a working DNS listener on port 53.");
        var initial = CheckedSnapshot();
        if (initial.Ipv4Index <= 0 || initial.Ipv6Index <= 0) throw new IOException("Private adapter is not ready for both IP families.");
        CheckConflicts();
        var commands = TunnelAdapterCommands.Build(initial.Ipv4Index, initial.Ipv6Index);
        foreach (var command in commands)
        {
            var current = CheckedSnapshot();
            if (current.Ipv4Index != initial.Ipv4Index || current.Ipv6Index != initial.Ipv6Index || !ownsDnsListener())
                throw new IOException("Private adapter or DNS listener changed during setup.");
            CheckConflicts();
            await execute(command, token);
        }
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = CheckedSnapshot();
            if (current.Ipv4Index != initial.Ipv4Index || current.Ipv6Index != initial.Ipv6Index || !ownsDnsListener())
                throw new IOException("Private adapter or DNS listener changed during setup.");
            if (current.Addresses.Contains(IPAddress.Parse("198.18.0.1")) &&
                current.Addresses.Contains(IPAddress.Parse("fd7a:6272:6964::1")) &&
                current.DnsServers.Count == 2 && current.DnsServers.Contains(IPAddress.Loopback) && current.DnsServers.Contains(IPAddress.IPv6Loopback)) { CheckConflicts(); return; }
            await Task.Delay(250, token);
        }
        throw new IOException("Private adapter configuration could not be verified.");
    }
    private TunnelAdapterState CheckedSnapshot()
    {
        var current = snapshot();
        if (current is null || current.AdapterId != adapterId || current.Luid != luid)
            throw new IOException("Private adapter identity changed.");
        return current;
    }
    private void CheckConflicts()
    {
        if (otherAddresses().Any(OverlapsReservedSubnet) || otherRoutes().Any(route => OverlapsReservedRoute(route.Prefix)))
            throw new IOException("Private tunnel subnet conflicts with another adapter or route.");
    }
    public static bool OverlapsReservedRoute(string prefix)
    {
        var split = prefix.Split('/');
        if (split.Length != 2 || !IPAddress.TryParse(split[0], out var address) || !int.TryParse(split[1], out var bits)) return true;
        var bytes = address.GetAddressBytes();
        var maxBits = bytes.Length * 8;
        if (bits < 0 || bits > maxBits) return true;
        if (bits == 0) return false; // Existing default routes are expected.
        var target = IPAddress.Parse(address.AddressFamily == AddressFamily.InterNetwork ? "198.18.0.1" : "fd7a:6272:6964::1").GetAddressBytes();
        var compareBits = Math.Min(bits, address.AddressFamily == AddressFamily.InterNetwork ? 24 : 64);
        for (var i = 0; i < compareBits; i++)
            if (((bytes[i / 8] ^ target[i / 8]) & (0x80 >> (i % 8))) != 0) return false;
        return true;
    }

    public static bool OverlapsReservedSubnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) return bytes[0] == 198 && bytes[1] == 18 && bytes[2] == 0;
        return address.AddressFamily == AddressFamily.InterNetworkV6 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xfd, 0x7a, 0x62, 0x72, 0x69, 0x64, 0, 0 });
    }
}
