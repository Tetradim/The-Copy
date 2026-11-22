using System.Globalization;
namespace TetherBridge.Desktop.FullTunnel;

// These commands target only the private TetherBridge adapter and active route store.
public static class TunnelRouteCommands
{
    public static IReadOnlyList<string> Ipv4Prefixes { get; } = Array.AsReadOnly(new[] { "0.0.0.0/1", "128.0.0.0/1" });
    public static IReadOnlyList<string> Ipv6Prefixes { get; } = Array.AsReadOnly(new[] { "::/1", "8000::/1" });
    public static string[] AddIpv4(int index, string prefix) => Route("ipv4", "add", index, prefix, "198.18.0.1", Ipv4Prefixes, true);
    public static string[] RenewIpv4(int index, string prefix) => Route("ipv4", "set", index, prefix, "198.18.0.1", Ipv4Prefixes, true);
    public static string[] DeleteIpv4(int index, string prefix) => Route("ipv4", "delete", index, prefix, "198.18.0.1", Ipv4Prefixes, false);
    public static string[] AddIpv6(int index, string prefix) => Route("ipv6", "add", index, prefix, "fd7a:6272:6964::1", Ipv6Prefixes, true);
    public static string[] RenewIpv6(int index, string prefix) => Route("ipv6", "set", index, prefix, "fd7a:6272:6964::1", Ipv6Prefixes, true);
    public static string[] DeleteIpv6(int index, string prefix) => Route("ipv6", "delete", index, prefix, "fd7a:6272:6964::1", Ipv6Prefixes, false);
    private static string[] Route(string family, string operation, int index, string prefix, string gateway, IReadOnlyList<string> allowed, bool lease)
    {
        if (index <= 0 || !allowed.Contains(prefix, StringComparer.Ordinal)) throw new ArgumentException("Invalid tunnel route identity.");
        var args = new List<string> { "interface", family, operation, "route", prefix, index.ToString(CultureInfo.InvariantCulture), gateway };
        if (lease) args.AddRange(["metric=1", "validlifetime=15", "preferredlifetime=15"]);
        args.Add("store=active"); return args.ToArray();
    }
}
