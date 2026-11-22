using System.Text;
namespace TetherBridge.Desktop.Transports;

public static class HttpProbeResponseReader
{
    public static async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        const int maxResponse = 65536;
        using var response = new MemoryStream();
        var buffer = new byte[2048]; int? expected = null; bool headerParsed = false;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            if (response.Length + count > maxResponse) throw new IOException("Phone response is too large.");
            response.Write(buffer, 0, count);
            if (!headerParsed)
            {
                var text = Encoding.ASCII.GetString(response.GetBuffer(), 0, (int)response.Length);
                var index = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (index >= 0)
                {
                    var header = text[..index];
                    int? length = null;
                    foreach (var line in header.Split("\r\n").Skip(1))
                    {
                        var colon = line.IndexOf(':'); if (colon < 0) throw new IOException("Invalid phone response header.");
                        var name = line[..colon].Trim(); var value = line[(colon + 1)..].Trim();
                        if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected response transfer encoding.");
                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            if (length.HasValue || !int.TryParse(value, out var parsed) || parsed < 0 || parsed > maxResponse - index - 4) throw new IOException("Invalid phone response length.");
                            length = parsed;
                        }
                    }
                    if (length.HasValue) expected = index + 4 + length.Value;
                    headerParsed = true;
                }
            }
            if (expected.HasValue && response.Length >= expected.Value)
                return Encoding.UTF8.GetString(response.GetBuffer(), 0, expected.Value);
        }
        if (!headerParsed || (expected.HasValue && response.Length < expected.Value)) throw new IOException("Incomplete phone response.");
        return Encoding.UTF8.GetString(response.ToArray());
    }
}
