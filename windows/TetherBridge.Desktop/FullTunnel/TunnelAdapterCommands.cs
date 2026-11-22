using System.Globalization;
namespace TetherBridge.Desktop.FullTunnel;

// Only for the ephemeral adapter identified by a freshly created engine.
public static class TunnelAdapterCommands
{
    public static string[][] Build(int ipv4Index, int ipv6Index)
    {
        if (ipv4Index <= 0 || ipv6Index <= 0) throw new ArgumentException("Private adapter indices must be positive.");
        var v4 = ipv4Index.ToString(CultureInfo.InvariantCulture);
        var v6 = ipv6Index.ToString(CultureInfo.InvariantCulture);
        return [
            ["interface", "ipv4", "set", "address", "name=" + v4, "source=static", "address=198.18.0.1", "mask=255.255.255.0", "gateway=none", "store=active"],
            ["interface", "ipv4", "set", "dnsservers", "name=" + v4, "source=static", "address=127.0.0.1", "register=none", "validate=no"],
            ["interface", "ipv6", "add", "address", "interface=" + v6, "address=fd7a:6272:6964::1/64", "store=active"],
            ["interface", "ipv6", "set", "dnsservers", "name=" + v6, "source=static", "address=::1", "register=none", "validate=no"]
        ];
    }
}
