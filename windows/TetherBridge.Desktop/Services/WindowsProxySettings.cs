using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace TetherBridge.Desktop.Services;

public sealed record ProxyState(
    int ProxyEnable,
    string? ProxyServer,
    string? ProxyOverride,
    string? AutoConfigUrl,
    int? AutoDetect);

public static class WindowsProxySettings
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxyState ReadCurrent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: false);
        if (key is null)
        {
            return new ProxyState(
                ProxyEnable: 0,
                ProxyServer: null,
                ProxyOverride: null,
                AutoConfigUrl: null,
                AutoDetect: null);
        }

        return new ProxyState(
            Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0),
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string,
            key.GetValue("AutoConfigURL") as string,
            key.GetValue("AutoDetect") is int i ? i : null);
    }

    public static void Restore(ProxyState state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true)
            ?? throw new InvalidOperationException("Could not create Windows Internet Settings registry key.");
        key.SetValue("ProxyEnable", state.ProxyEnable, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", state.ProxyServer);
        SetOrDelete(key, "ProxyOverride", state.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", state.AutoConfigUrl);
        if (state.AutoDetect.HasValue) key.SetValue("AutoDetect", state.AutoDetect.Value, RegistryValueKind.DWord);
        else key.DeleteValue("AutoDetect", throwOnMissingValue: false);
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
    }

    public static void EnableLocalProxy(string host, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true)
            ?? throw new InvalidOperationException("Could not create Windows Internet Settings registry key.");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"http={host}:{port};https={host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    public static bool PointsAtLocalProxy(string host, int port)
    {
        var state = ReadCurrent();
        return IsAppliedLocalProxy(state, host, port);
    }

    public static bool ReferencesLocalProxy(string host, int port)
    {
        var state = ReadCurrent();
        return state.ProxyEnable == 1 && ProxyServerContainsEndpoint(state.ProxyServer, host, port);
    }

    public static bool DisableIfLocalProxy(string host, int port)
    {
        var state = ReadCurrent();
        if (!IsAppliedLocalProxy(state, host, port)) return false;
        Restore(state with { ProxyEnable = 0, ProxyServer = null });
        return true;
    }

    // Match every field set by EnableLocalProxy before restoring a whole snapshot.
    // AutoDetect is intentionally excluded because enabling the proxy does not set it.
    public static bool IsAppliedLocalProxy(ProxyState state, string host, int port) =>
        state.ProxyEnable == 1 &&
        string.Equals(state.ProxyServer, $"http={host}:{port};https={host}:{port}", StringComparison.OrdinalIgnoreCase) &&
        state.ProxyOverride == "<local>" && state.AutoConfigUrl is null;

    public static bool ProxyServerContainsEndpoint(string? proxyServer, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;
        var endpoint = $"{host}:{port}";
        return proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals(endpoint, StringComparison.OrdinalIgnoreCase) || part.EndsWith("=" + endpoint, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
