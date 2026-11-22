using System.Net.NetworkInformation;
using System.Text.Json;
using TetherBridge.Desktop.FullTunnel;

internal sealed record LabNetworkState(LabAdapter[] Adapters, LabRoute[] Routes)
{
    public static LabNetworkState Capture()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
            .Select(adapter =>
            {
                var properties = adapter.GetIPProperties();
                return new LabAdapter(adapter.Id, adapter.Name,
                    properties.DnsAddresses.Select(address => address.ToString()).ToArray());
            }).ToArray();
        var routes = WindowsRouteTable.Read()
            .Select(route => new LabRoute(route.InterfaceLuid, route.InterfaceIndex,
                route.Prefix, route.NextHop.ToString(), route.Metric))
            .OrderBy(route => route.Luid).ThenBy(route => route.Index)
            .ThenBy(route => route.Prefix, StringComparer.Ordinal)
            .ThenBy(route => route.NextHop, StringComparer.Ordinal)
            .ThenBy(route => route.Metric).ToArray();
        return new LabNetworkState(adapters, routes);
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

internal sealed record LabAdapter(string Id, string Name, string[] DnsServers);
internal sealed record LabRoute(ulong Luid, int Index, string Prefix, string NextHop, uint Metric);
