using System.Globalization;
namespace TetherBridge.Desktop.FullTunnel;

public sealed class NetshRouteRunner(int ownedIpv4Index, int ownedIpv6Index, IPrivateRouteInspector inspector) : IRouteCommandRunner
{
    public static bool IsAllowed(string[] args, int ownedIpv4Index, int ownedIpv6Index)
    {
        if (args.Length < 8 || !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index <= 0 || index != (args[1] == "ipv6" ? ownedIpv6Index : ownedIpv4Index)) return false;
        foreach (var prefix in TunnelRouteCommands.Ipv4Prefixes)
            if (args.SequenceEqual(TunnelRouteCommands.AddIpv4(index, prefix)) || args.SequenceEqual(TunnelRouteCommands.RenewIpv4(index, prefix)) || args.SequenceEqual(TunnelRouteCommands.DeleteIpv4(index, prefix))) return true;
        foreach (var prefix in TunnelRouteCommands.Ipv6Prefixes)
            if (args.SequenceEqual(TunnelRouteCommands.AddIpv6(index, prefix)) || args.SequenceEqual(TunnelRouteCommands.RenewIpv6(index, prefix)) || args.SequenceEqual(TunnelRouteCommands.DeleteIpv6(index, prefix))) return true;
        return false;
    }
    public Task<PrivateRouteState> InspectAsync(int index, string prefix, bool ipv6, CancellationToken token) =>
        inspector.InspectAsync(index, prefix, ipv6, token);

    public Task PrepareAsync(int index, string prefix, bool ipv6, CancellationToken token) =>
        inspector.PrepareAsync(index, prefix, ipv6, token);

    public async Task RunAsync(string[] args, CancellationToken token)
    {
        if (!IsAllowed(args, ownedIpv4Index, ownedIpv6Index)) throw new ArgumentException("Route command is not permitted.");
        var state = await inspector.InspectAsync(args[1] == "ipv6" ? ownedIpv6Index : ownedIpv4Index, args[4], args[1] == "ipv6", token);
        if ((args[2] == "add" && state != PrivateRouteState.Absent) || (args[2] != "add" && state != PrivateRouteState.Owned))
            throw new IOException("Private adapter or route ownership changed.");
        var exitCode = await TunnelCommandProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            args, TimeSpan.FromSeconds(5), token);
        if (exitCode != 0) throw new IOException($"Route configuration failed (netsh exit code {exitCode}).");
    }
}
