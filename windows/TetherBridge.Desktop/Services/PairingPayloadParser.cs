namespace TetherBridge.Desktop.Services;

public static class PairingPayloadParser
{
    public static bool TryParse(string text, out PairingPayload payload, out string message)
    {
        payload = new PairingPayload("", 10888, "", "wifi");
        message = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            message = "PAIRING_EMPTY: Paste the Android pairing payload or manually enter host/token.";
            return false;
        }

        text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("tetherbridge", StringComparison.OrdinalIgnoreCase) || !uri.Host.Equals("pair", StringComparison.OrdinalIgnoreCase))
        {
            message = $"PAIRING_UNSUPPORTED: Expected {PairingPayload.SchemePrefix}?... payload. Manual host/token entry still works.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        var host = query.GetValueOrDefault("host", "").Trim();
        var token = query.GetValueOrDefault("token", "").Trim();
        var mode = query.GetValueOrDefault("mode", "wifi").Trim();
        var fingerprint = query.GetValueOrDefault("fingerprint", "").Trim().ToUpperInvariant();
        var portText = query.GetValueOrDefault("port", "10888").Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            message = "PAIRING_INCOMPLETE: Payload is missing host or token.";
            return false;
        }
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            message = "PAIRING_BAD_PORT: Payload port is not valid.";
            return false;
        }

        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            message = "PAIRING_FINGERPRINT_REQUIRED: Pair using a current Android payload containing its certificate fingerprint.";
            return false;
        }

        payload = new PairingPayload(host, port, token, mode) { Fingerprint = fingerprint };
        message = $"PAIRING_OK: Loaded {mode} pairing for {host}:{port}.";
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : "";
            result[key] = value;
        }
        return result;
    }
}
