using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TetherBridge.Desktop.Services;

public sealed class WifiHotspotInspector
{
    public IReadOnlyList<DiagnosticResult> Check()
    {
        var results = new List<DiagnosticResult>();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        var wifi = adapters.Where(a =>
            a.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            a.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            a.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (wifi.Length == 0)
        {
            results.Add(DiagnosticResult.Fail("WIFI_ADAPTER_NOT_UP", "No active Wi-Fi adapter was found.", "NetworkInterface enumeration", "Turn on Wi-Fi and connect to the phone hotspot or Wi-Fi Direct group."));
            return results;
        }

        foreach (var adapter in wifi)
        {
            var ipProps = adapter.GetIPProperties();
            var gateways = ipProps.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();
            var gatewayText = gateways.Length == 0 ? "no IPv4 gateway" : string.Join(", ", gateways);
            var commonPhoneGateway = gateways.Any(g => g is "192.168.43.1" or "192.168.49.1" or "172.20.10.1");
            if (commonPhoneGateway)
                results.Add(DiagnosticResult.Pass("PHONE_HOTSPOT_GATEWAY_CANDIDATE", $"Wi-Fi adapter {adapter.Name} has a common phone gateway: {gatewayText}", "Gateway inspection"));
            else
                results.Add(DiagnosticResult.Pass("WIFI_ADAPTER_UP", $"Wi-Fi adapter {adapter.Name} is up; gateways: {gatewayText}", "Gateway inspection"));
        }

        results.Add(DiagnosticResult.Fail("WIFI_BRIDGE_GUIDANCE", "Use the Android QR/pairing payload, then make sure this laptop is on the same phone-created or phone-reachable network.", "User setup", "If health fails, verify laptop Wi-Fi network, phone IP, and token."));
        return results;
    }
}
