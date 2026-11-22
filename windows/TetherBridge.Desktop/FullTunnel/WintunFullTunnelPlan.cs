namespace TetherBridge.Desktop.FullTunnel;

/// <summary>
/// v0.7+ design placeholder. Full tunnel must live behind an elevated Windows service.
/// The normal desktop UI should never directly mutate adapters, routes, DNS, or firewall state.
/// </summary>
public sealed class WintunFullTunnelPlan
{
    public string TargetArchitecture =>
        "Windows TCP/IP -> Wintun adapter -> tun2socks/gVisor engine -> local SOCKS5 relay -> phone bridge";

    public string RecommendedProxyEndpoint => "socks5://127.0.0.1:10810";

    public bool RequiresElevation => true;

    public IReadOnlyList<string> ServiceSteps => new[]
    {
        "Verify proxy-mode phone bridge health before starting full tunnel.",
        "Persist route, DNS, interface metric, WinINet, and WinHTTP snapshots.",
        "Create or open a TetherBridge Wintun adapter from an elevated service.",
        "Assign tunnel interface address and DNS policy.",
        "Protect the phone transport with a host route outside the tunnel.",
        "Start tun2socks pointed at the local SOCKS5 relay endpoint.",
        "Switch default IPv4/IPv6 routes to the tunnel adapter.",
        "Monitor phone bridge, tun2socks, Wintun session, and DNS health.",
        "Restore all network state on disconnect, failed start, crash recovery, or service startup repair."
    };

    public IReadOnlyList<string> ShipBlockers => new[]
    {
        "SOCKS5 local relay endpoint is implemented and tested.",
        "DNS snapshot/restore and IPv6 route policy are implemented.",
        "Phone host route remains outside the tunnel.",
        "tun2socks process failure restores network state.",
        "Installer/uninstaller recovery tests pass on real Windows machines."
    };
}
