namespace TetherBridge.Desktop.Services;

public static class ModeGuidanceService
{
    public static string WifiGuidance =>
        "Wi-Fi Bridge: start Wi-Fi Bridge on Android, connect Windows to the same phone hotspot/LAN/PAN-reachable network, scan/paste the pairing payload, then click Start Wi-Fi. This is not Wi-Fi Direct group formation yet.";

    public static string BluetoothGuidance =>
        "Bluetooth/PAN Assist: pair the phone in Windows Bluetooth settings, enable Android Bluetooth tethering if available, then run PAN diagnostics. If PAN gives the phone an IP, paste that IP/token into Wi-Fi Bridge. Custom RFCOMM tunnel is still a scaffold, not an exposed mode.";

    public static string UsbGuidance =>
        "USB Bridge: enable Android developer options and USB debugging, connect by cable, accept the phone's ADB authorization prompt, start Android USB Bridge, then Start USB in Windows.";
}
