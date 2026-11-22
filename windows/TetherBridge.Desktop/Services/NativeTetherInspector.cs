using System.Net.NetworkInformation;

namespace TetherBridge.Desktop.Services;

public sealed class NativeTetherInspector
{
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
            if (desc.Contains("remote ndis") || desc.Contains("usb") || desc.Contains("android"))
                yield return DiagnosticResult.Pass("NATIVE_USB_TETHER_CANDIDATE", "Possible native USB tethering adapter: " + a.Name);
            if (desc.Contains("wi-fi") || desc.Contains("wireless"))
                yield return DiagnosticResult.Pass("NATIVE_WIFI_CANDIDATE", "Possible Wi-Fi/hotspot adapter: " + a.Name);
            if (desc.Contains("bluetooth") || desc.Contains("pan"))
                yield return DiagnosticResult.Pass("NATIVE_BLUETOOTH_CANDIDATE", "Possible Bluetooth/PAN adapter: " + a.Name);
        }
    }
}
