using System.Net.NetworkInformation;

namespace TetherBridge.Desktop.Services;

public sealed class BluetoothPanInspector
{
    public DiagnosticResult CheckPanAvailability()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                        n.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                        n.Description.Contains("PAN", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (adapters.Length == 0)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_UNAVAILABLE", "Windows does not show a Bluetooth PAN adapter.", "Enumerate network adapters", "Pair the phone and enable Bluetooth tethering/PAN on the phone.");

        var up = adapters.FirstOrDefault(a => a.OperationalStatus == OperationalStatus.Up);
        if (up is null)
            return DiagnosticResult.Fail("BLUETOOTH_PAN_DOWN", "Bluetooth PAN adapter exists but is not connected.", "NetworkInterface.OperationalStatus", "Connect to the phone's Bluetooth network from Windows Settings.");

        return DiagnosticResult.Pass("BLUETOOTH_PAN_OK", "Bluetooth PAN adapter is up: " + up.Name, "NetworkInterface enumeration");
    }
}
