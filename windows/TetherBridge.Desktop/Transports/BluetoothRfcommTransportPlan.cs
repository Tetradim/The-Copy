namespace TetherBridge.Desktop.Transports;

public static class BluetoothRfcommTransportPlan
{
    public const string ServiceUuid = "7b8f2a00-35be-4fa3-b8d3-4a9d5b96db61";

    public static string Guidance =>
        "RFCOMM socket transport is reserved for v0.7+. v0.6.4 keeps Bluetooth honest: it detects PAN/native tethering and documents the socket UUID, but it does not expose a fake Bluetooth data path.";
}
