namespace TetherBridge.Desktop.FullTunnel;

/// <summary>
/// v0.7+ command planning only. This class intentionally does not launch processes in v0.6.4.
/// </summary>
public static class Tun2SocksLaunchPlan
{
    public static IReadOnlyList<string> BuildChecklist() => new[]
    {
        "Confirm local relay exposes a SOCKS5-compatible endpoint.",
        "Confirm Wintun DLL is present and verified.",
        "Create/open Wintun adapter from elevated service.",
        "Assign static adapter address and DNS policy.",
        "Add protected route to phone bridge outside the tunnel.",
        "Launch tun2socks with device=wintun and proxy=socks5://127.0.0.1:<relay-port>.",
        "Set default route through Wintun adapter.",
        "Monitor process, adapter, DNS, route, and phone bridge health.",
        "Restore all snapshots on stop or failure."
    };
}
