using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TetherBridge.Desktop.FullTunnel;

namespace TetherBridge.Desktop.Services;

public sealed class NativeTetherInspector
{
    public DiagnosticResult CheckUsbRoute()
    {
        const string setup = "Connect a data-capable USB cable, unlock Android, and enable USB tethering under Hotspot & tethering if your device offers it.";
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => IsNativeUsbCandidate(adapter.Description)).ToArray();
        if (adapters.Length == 0)
            return DiagnosticResult.Fail("NATIVE_USB_NOT_DETECTED", "Windows has not identified a USB tether network adapter.",
                "Windows network adapter inventory", setup);

        var connected = adapters.Where(adapter => adapter.OperationalStatus == OperationalStatus.Up).ToArray();
        if (connected.Length == 0)
            return DiagnosticResult.Fail("NATIVE_USB_DISCONNECTED", "Windows detected a USB tether adapter, but it is disconnected.",
                "Windows network adapter status", setup);

        var candidates = connected.Select(adapter => (adapter.Name, Ip: adapter.GetIPProperties()))
            .Where(item => item.Ip.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(gateway.Address))).ToArray();
        if (candidates.Length == 0)
            return DiagnosticResult.Fail("NATIVE_USB_NO_GATEWAY", "The USB tether adapter is up but has no IPv4 gateway.",
                "Windows adapter IP configuration", "Wait for Android to assign network settings or reconnect in a test window.");

        int preferred;
        try { preferred = WindowsRouteTable.GetPreferredInterface(IPAddress.Parse("1.1.1.1")); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return DiagnosticResult.Fail("NATIVE_USB_NO_ROUTE", "Windows could not select an IPv4 route: " + ex.Message,
                "Windows route lookup", "Check whether the Android USB tether connection has finished configuring.");
        }
        var selected = candidates.FirstOrDefault(item => item.Ip.GetIPv4Properties()?.Index == preferred);
        if (selected.Ip is null)
            return DiagnosticResult.Fail("NATIVE_USB_OTHER_ROUTE", "A USB tether candidate has a gateway, but Windows prefers another adapter.",
                "Windows route lookup", "Verify native USB Internet access in an isolated test window.");
        return DiagnosticResult.Pass("NATIVE_USB_ROUTE_OK",
            $"Windows prefers the USB tether candidate {selected.Name} for the IPv4 test destination. This does not identify the handset or prove Internet access.",
            "Windows route lookup");
    }

    internal static bool IsNativeUsbCandidate(string description) =>
        new[] { "Remote NDIS", "RNDIS", "NCM Host", "CDC NCM", "CDC-NCM", "CDC ECM", "Android USB Ethernet", "USB Tether" }
            .Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<DiagnosticResult> Check()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        if (adapters.Length == 0)
        {
            yield return DiagnosticResult.Fail("NO_ACTIVE_NETWORK", "Windows has no active network adapters.", "NetworkInterface enumeration", "Enable USB tethering, Wi-Fi hotspot, or Bluetooth tethering on the phone.");
            yield break;
        }

        foreach (var a in adapters)
        {
            var desc = (a.Name + " " + a.Description).ToLowerInvariant();
            if (IsNativeUsbCandidate(a.Description))
                yield return DiagnosticResult.Pass("NATIVE_USB_TETHER_CANDIDATE", "Possible native USB tethering adapter: " + a.Name);
            if (desc.Contains("wi-fi") || desc.Contains("wireless"))
                yield return DiagnosticResult.Pass("NATIVE_WIFI_CANDIDATE", "Possible Wi-Fi/hotspot adapter: " + a.Name);
            if (desc.Contains("bluetooth") || desc.Contains("pan"))
                yield return DiagnosticResult.Pass("NATIVE_BLUETOOTH_CANDIDATE", "Possible Bluetooth/PAN adapter: " + a.Name);
        }
    }
}
