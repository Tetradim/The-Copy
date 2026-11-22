using System.Text.RegularExpressions;

namespace TetherBridge.Desktop.Services;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public override string ToString() => $"{Model} ({Serial}) — {State}";
}

public static class AdbDeviceCatalog
{
    public static AdbDevice[] Parse(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && Regex.IsMatch(parts[0], @"^[A-Za-z0-9][A-Za-z0-9_.:-]*$") &&
            new[] { "device", "unauthorized", "offline", "recovery", "sideload", "rescue" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
        .Select(parts => new AdbDevice(parts[0], parts[1].ToLowerInvariant(),
            (parts.Skip(2).FirstOrDefault(x => x.StartsWith("model:", StringComparison.OrdinalIgnoreCase))?[6..] ?? "Android device").Replace('_', ' ')))
        .ToArray();

    public static (DiagnosticResult Result, string? Serial) Select(AdbDevice[] devices, string? preferred)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            var selected = devices.FirstOrDefault(x => x.Serial.Equals(preferred, StringComparison.Ordinal));
            if (selected is null) return (DiagnosticResult.Fail("ADB_SELECTED_MISSING", "The selected Android device is not connected.", "adb devices", "Reconnect it or select another device."), null);
            if (selected.State == "unauthorized") return (DiagnosticResult.Fail("ADB_UNAUTHORIZED", "The selected Android device has not authorized this computer.", "adb devices", "Unlock it and accept the USB debugging prompt."), null);
            if (selected.State != "device") return (DiagnosticResult.Fail("ADB_NO_READY_DEVICE", "The selected Android device is not ready.", "adb devices", "Reconnect it and authorize USB debugging."), null);
            return (DiagnosticResult.Pass("ADB_OK", $"Selected {selected.Model}."), selected.Serial);
        }
        var ready = devices.Where(x => x.State == "device").ToArray();
        if (ready.Length == 1) return (DiagnosticResult.Pass("ADB_OK", $"Selected {ready[0].Model}."), ready[0].Serial);
        if (ready.Length > 1) return (DiagnosticResult.Fail("ADB_MULTIPLE_DEVICES", "More than one Android device is ready.", "adb devices", "Choose the device in the USB device list."), null);
        if (devices.Any(x => x.State == "unauthorized")) return (DiagnosticResult.Fail("ADB_UNAUTHORIZED", "An Android device has not authorized this computer.", "adb devices", "Unlock it and accept the USB debugging prompt."), null);
        return (DiagnosticResult.Fail(devices.Length == 0 ? "ADB_NO_DEVICE" : "ADB_NO_READY_DEVICE", "No authorized Android device is ready.", "adb devices", "Check the cable, unlock the device, and enable USB debugging."), null);
    }
}
