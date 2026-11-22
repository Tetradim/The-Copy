using System.Net.Sockets;

namespace TetherBridge.Desktop.Services;

public sealed class DiagnosticsService
{
    private readonly string? _selectedSerial;
    public DiagnosticsService(string? selectedSerial = null) => _selectedSerial = selectedSerial;

    public async Task<IReadOnlyList<DiagnosticResult>> RunAllAsync()
    {
        var results = new List<DiagnosticResult>();
        results.Add(await new AdbService(_selectedSerial).CheckAsync());
        results.Add(CheckLocalProxy());
        results.Add(CheckProxyState());
        results.Add(await new WinHttpProxyInspector().CheckAsync());
        results.AddRange(new WifiHotspotInspector().Check());
        results.Add(new BluetoothPanInspector().CheckPanAvailability());
        results.AddRange(new NativeTetherInspector().Check());
        return results;
    }

    private DiagnosticResult CheckLocalProxy()
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", 10809);
            if (!task.Wait(TimeSpan.FromMilliseconds(700)))
                return DiagnosticResult.Fail("LOCAL_PROXY_DOWN", "Windows local relay is not listening.", "TCP 127.0.0.1:10809", "Start a bridge mode from the companion.");
            return DiagnosticResult.Pass("LOCAL_PROXY_OK", "Windows local relay is listening.", "TCP 127.0.0.1:10809");
        }
        catch
        {
            return DiagnosticResult.Fail("LOCAL_PROXY_DOWN", "Windows local relay is not listening.", "TCP 127.0.0.1:10809", "Start a bridge mode from the companion.");
        }
    }

    private DiagnosticResult CheckProxyState()
    {
        var state = WindowsProxySettings.ReadCurrent();
        if (WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", 10809))
            return DiagnosticResult.Pass("WINDOWS_PROXY_ENABLED", "Windows proxy points to TetherBridge local relay.", "HKCU Internet Settings");
        if (state.ProxyEnable == 1)
            return DiagnosticResult.Fail("WINDOWS_PROXY_OTHER", "Windows proxy is enabled but not pointing to TetherBridge.", "HKCU Internet Settings", "Do not overwrite without confirmation; save current settings first.");
        return DiagnosticResult.Pass("WINDOWS_PROXY_DISABLED", "Windows current-user proxy is disabled.", "HKCU Internet Settings");
    }
}
