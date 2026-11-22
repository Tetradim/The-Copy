using System.Net;
using System.Net.NetworkInformation;

namespace TetherBridge.Desktop.FullTunnel;

// Only used with a fresh, owned tunnel engine. This class is not wired to the UI.
public sealed class WindowsTunnelAdapterBackend(Guid adapterId, ulong luid, int ipv4Index, int ipv6Index, Func<bool> engineAlive)
{
    public static bool IsAllowed(string[] command, int ipv4Index, int ipv6Index) =>
        ipv4Index > 0 && ipv6Index > 0 && TunnelAdapterCommands.Build(ipv4Index, ipv6Index).Any(expected => command.SequenceEqual(expected));

    public TunnelAdapterState? Read()
    {
        if (!engineAlive()) return null;
        var identity = WindowsPrivateAdapterSnapshot.Read(adapterId);
        if (identity is null || identity.Luid != luid || identity.Ipv4Index != ipv4Index || identity.Ipv6Index != ipv6Index) return null;
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => Guid.TryParse(item.Id, out var id) && id == adapterId);
        if (adapter is null) return null;
        var properties = adapter.GetIPProperties();
        return new TunnelAdapterState(adapterId, luid, ipv4Index, ipv6Index,
            properties.UnicastAddresses.Select(item => item.Address).ToArray(), properties.DnsAddresses.ToArray());
    }

    public IReadOnlyCollection<IPAddress> ReadOtherAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(item => !Guid.TryParse(item.Id, out var id) || id != adapterId)
        .SelectMany(item => item.GetIPProperties().UnicastAddresses.Select(address => address.Address)).ToArray();

    public IReadOnlyCollection<WindowsRoute> ReadOtherRoutes() => WindowsRouteTable.Read().Where(route => route.InterfaceLuid != luid).ToArray();

    public async Task ExecuteAsync(string[] command, CancellationToken token)
    {
        if (!IsAllowed(command, ipv4Index, ipv6Index) || Read() is null) throw new IOException("Private adapter identity or command was rejected.");
        var exitCode = await TunnelCommandProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            command, TimeSpan.FromSeconds(5), token);
        if (exitCode != 0) throw new IOException($"Private adapter configuration failed (netsh exit code {exitCode}).");
        if (Read() is null) throw new IOException("Private adapter changed during configuration.");
    }
}
