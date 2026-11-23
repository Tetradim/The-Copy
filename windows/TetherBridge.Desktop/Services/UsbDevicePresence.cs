using Windows.Devices.Enumeration;

namespace TetherBridge.Desktop.Services;

public static class UsbDevicePresence
{
    private const string PortableDeviceSelector =
        "System.Devices.InterfaceClassGuid:={6AC27878-A6FA-4155-BA85-F98F491D4F33} AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    public static async Task<DiagnosticResult> CheckAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            var devices = await DeviceInformation.FindAllAsync(PortableDeviceSelector).AsTask(deadline.Token);
            // WPD can also enumerate non-USB devices. Only count hardware-backed USB interfaces here.
            var names = devices.Where(device => device.Id.StartsWith(@"\\?\usb#", StringComparison.OrdinalIgnoreCase))
                .Select(device => device.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().Take(4).ToArray();
            if (names.Length == 0)
                return DiagnosticResult.Fail("USB_PORTABLE_NOT_SEEN", "Windows did not report a USB portable-device interface.",
                    "Windows Portable Devices", "If ADB also cannot see the phone, unlock it, try File transfer temporarily, and check the cable and port. An ADB or tether-only mode may expose no portable-device interface.");
            return DiagnosticResult.Pass("USB_PORTABLE_SEEN", $"Windows sees USB data devices: {string.Join(", ", names)}.",
                "Windows Portable Devices");
        }
        catch (Exception ex)
        {
            return DiagnosticResult.Fail("USB_PORTABLE_CHECK_FAILED", "Could not read the Windows USB portable-device list.",
                "Windows Portable Devices", ex.Message);
        }
    }
}
