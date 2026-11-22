using System.Text.Json;
namespace TetherBridge.Desktop.Services;

public static class PhoneCapabilities
{
    public static bool SupportsUdp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("capabilities", out var values) &&
                values.ValueKind == JsonValueKind.Array &&
                values.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "udp");
        }
        catch (JsonException) { return false; }
    }
}
