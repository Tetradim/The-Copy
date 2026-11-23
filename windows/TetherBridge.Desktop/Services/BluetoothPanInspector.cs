using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TetherBridge.Desktop.FullTunnel;

namespace TetherBridge.Desktop.Services;

public sealed class BluetoothPanInspector
{
    public DiagnosticResult CheckPanAvailability()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                              adapter.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        const string setup = "Enable Bluetooth tethering on Android, then open Windows Bluetooth devices and join the phone's Personal Area Network as an Access Point.";
        if (adapters.Length == 0)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_UNAVAILABLE", "Windows does not show a Bluetooth network adapter.", "Network adapter inventory", setup);

        var connected = adapters.Where(adapter => adapter.OperationalStatus == OperationalStatus.Up).ToArray();
        if (connected.Length == 0)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_DOWN", "Windows shows a Bluetooth network adapter, but it is disconnected.", "Network adapter status", setup);

        var routed = connected.Select(adapter => (adapter.Name, Ip: adapter.GetIPProperties()))
            .Where(item => item.Ip.GatewayAddresses.Any(gateway => IsGateway(gateway.Address) &&
                gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .ToArray();
        if (routed.Length == 0)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_NO_GATEWAY", "The Bluetooth adapter is up but has no IPv4 gateway.", "Bluetooth adapter IP configuration", "Wait for the phone to assign network settings. Check that Bluetooth tethering is enabled on the phone.");

        var preferred = WindowsRouteTable.GetPreferredInterface(IPAddress.Parse("1.1.1.1"));
        var selected = routed.FirstOrDefault(item => item.Ip.GetIPv4Properties()?.Index == preferred);
        if (selected.Ip is null)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_OTHER_ROUTE", "Bluetooth PAN has an IPv4 gateway, but Windows prefers another adapter for internet traffic.", "Windows route lookup", "Use a test computer or a planned test window to verify the Bluetooth internet path.");

        return DiagnosticResult.Pass("BLUETOOTH_PAN_ROUTE_OK",
            $"Windows prefers {selected.Name} for the IPv4 test destination. Internet reachability still needs a traffic test.",
            "Windows route lookup");
    }

    private static bool IsGateway(IPAddress address) =>
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address);
}
