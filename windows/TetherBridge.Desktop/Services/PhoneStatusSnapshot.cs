using System.Text.Json;

namespace TetherBridge.Desktop.Services;

public sealed record PhoneStatusSnapshot(
    string Mode,
    string ListenHost,
    int ProxyPort,
    int ActiveConnections,
    long TotalConnections,
    long BytesUp,
    long BytesDown,
    long UptimeMs,
    int PoolActive,
    int PoolQueueDepth,
    int ControlActive,
    int ControlQueueDepth)
{
    public string ToDisplayString()
    {
        var uptime = TimeSpan.FromMilliseconds(UptimeMs);
        return $"Android bridge: mode={Mode}, listen={ListenHost}:{ProxyPort}, active={ActiveConnections}, total={TotalConnections}, pool={PoolActive}/{PoolQueueDepth}, control={ControlActive}/{ControlQueueDepth}, up={BytesUp:N0} bytes, down={BytesDown:N0} bytes, uptime={uptime:g}";
    }

    public static bool TryParse(string json, out PhoneStatusSnapshot snapshot, out string error)
    {
        snapshot = new PhoneStatusSnapshot("unknown", "unknown", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            snapshot = new PhoneStatusSnapshot(
                GetString(root, "mode", "unknown"),
                GetString(root, "listenHost", "unknown"),
                GetInt(root, "proxyPort"),
                GetInt(root, "activeConnections"),
                GetLong(root, "totalConnections"),
                GetLong(root, "bytesUp"),
                GetLong(root, "bytesDown"),
                GetLong(root, "uptimeMs"),
                GetInt(root, "poolActive"),
                GetInt(root, "poolQueueDepth"),
                GetInt(root, "controlActive"),
                GetInt(root, "controlQueueDepth"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
