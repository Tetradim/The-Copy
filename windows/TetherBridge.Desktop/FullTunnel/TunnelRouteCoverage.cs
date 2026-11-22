using System.Net;
using System.Net.Sockets;

namespace TetherBridge.Desktop.FullTunnel;

// Local/private/link-local and multicast routes are deliberately retained for LAN access.
// A public route more specific than the proposed /1 routes could bypass the tunnel.
public static class TunnelRouteCoverage
{
    private static readonly (IPAddress Address, int Bits)[] Exempt = new[]
    {
        (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16), (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("127.0.0.0"), 8), (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("255.255.255.255"), 32), (IPAddress.Parse("0.0.0.0"), 32),
        (IPAddress.IPv6Loopback, 128), (IPAddress.Parse("fe80::"), 10),
        (IPAddress.Parse("ff00::"), 8), (IPAddress.Parse("fc00::"), 7)
    };
    public static bool CanBypass(string prefix)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var bits)) return true;
        var bytes = address.GetAddressBytes();
        if (bits < 0 || bits > bytes.Length * 8) return true;
        if (bits == 0) return false; // A /1 tunnel route outranks a default route.
        foreach (var (local, localBits) in Exempt)
        {
            if (local.AddressFamily != address.AddressFamily || bits < localBits) continue;
            var baseBytes = local.GetAddressBytes(); var matches = true;
            for (var i = 0; i < localBits; i++)
                if (((bytes[i / 8] ^ baseBytes[i / 8]) & (0x80 >> (i % 8))) != 0) { matches = false; break; }
            if (matches) return false;
        }
        return true;
    }
}
