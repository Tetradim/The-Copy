using System.Net;
using System.Net.Sockets;
using System.Text;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop.Services;

public sealed record LocalRelayStatsSnapshot(int ActiveConnections, long TotalConnections, long BytesUp, long BytesDown)
{
    public string ToDisplayString() => $"active={ActiveConnections}, total={TotalConnections}, up={BytesUp:N0} bytes, down={BytesDown:N0} bytes";
}


public sealed class LocalRelayProxy
{
    private readonly SemaphoreSlim _connectionLimit = new(128, 128);
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private long _bytesUp;
    private long _bytesDown;
    private long _totalConnections;
    private int _activeConnections;

    public bool IsRunning => _listener is not null;

    public void Start(PhoneBridgeSession session, int localPort, Action<string> log)
    {
        Stop();
        _bytesUp = 0;
        _bytesDown = 0;
        _totalConnections = 0;
        _activeConnections = 0;
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, localPort) { ExclusiveAddressUse = true };
        try { listener.Start(64); }
        catch { listener.Stop(); cts.Dispose(); throw; }
        _cts = cts;
        _listener = listener;
        var token = cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, session, log, token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts = null;
        _listener = null;
    }

    public LocalRelayStatsSnapshot GetSnapshot()
    {
        return new LocalRelayStatsSnapshot(
            Volatile.Read(ref _activeConnections),
            Volatile.Read(ref _totalConnections),
            Volatile.Read(ref _bytesUp),
            Volatile.Read(ref _bytesDown));
    }

    public string GetStatusLine() => "LOCAL_RELAY_STATS: " + GetSnapshot().ToDisplayString();

    private async Task AcceptLoopAsync(TcpListener listener, PhoneBridgeSession session, Action<string> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (!_connectionLimit.Wait(0))
                {
                    client.Close();
                    log("LOCAL_RELAY_BUSY: Connection limit reached; rejected a local HTTP proxy client.");
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(client, session, log, ct); }
                    finally { _connectionLimit.Release(); }
                });
            }
            catch when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log("LOCAL_PROXY_ACCEPT_ERROR: " + ex.Message); }
        }
    }

    private async Task HandleAsync(TcpClient client, PhoneBridgeSession session, Action<string> log, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        using var _ = client;
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown-client";
        var requestLine = "no-request-line";
        try
        {
            var clientStream = client.GetStream();
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerCts.CancelAfter(TimeSpan.FromSeconds(30));
            var headerRead = await ReadHeaderAsync(clientStream, headerCts.Token);
            if (headerRead.Header.Length == 0) return;

            requestLine = FirstLine(headerRead.Header);
            var rewritten = AddTokenHeaders(headerRead.Header, session.Token);
            await using var phone = await ConnectPhoneAsync(session, ct);
            await phone.WriteAsync(rewritten, ct);
            Interlocked.Add(ref _bytesUp, rewritten.Length);
            if (headerRead.Tail.Length > 0)
            {
                await phone.WriteAsync(headerRead.Tail, ct);
                Interlocked.Add(ref _bytesUp, headerRead.Tail.Length);
            }

            await DuplexRelay.RunAsync(clientStream, client.Client, phone,
                n => Interlocked.Add(ref _bytesUp, n), n => Interlocked.Add(ref _bytesDown, n), ct);
        }
        catch (Exception ex)
        {
            log($"LOCAL_RELAY_CONNECTION_ERROR: client={clientEndpoint}, request=\"{requestLine}\", error={DescribeException(ex)}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task<Stream> ConnectPhoneAsync(PhoneBridgeSession session, CancellationToken ct)
    {
        // Keep one failed client connection local to that client. The session
        // monitor owns whole-session teardown after repeated health failures.
        return await session.Transport.ConnectAsync(ct);
    }

    private sealed record HeaderReadResult(byte[] Header, byte[] Tail);

    private static async Task<HeaderReadResult> ReadHeaderAsync(Stream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var readBuffer = new byte[4096];
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;

        while (bytes.Length < 64 * 1024)
        {
            var remaining = (int)Math.Min(readBuffer.Length, 64 * 1024 - bytes.Length);
            var n = await stream.ReadAsync(readBuffer.AsMemory(0, remaining), ct);
            if (n == 0) break;
            for (var i = 0; i < n; i++)
            {
                var b = readBuffer[i];
                bytes.WriteByte(b);
                matched = b == terminator[matched]
                    ? matched + 1
                    : b == terminator[0] ? 1 : 0;
                if (matched == terminator.Length)
                {
                    var tailLength = n - i - 1;
                    var tail = tailLength > 0 ? readBuffer[(i + 1)..n] : Array.Empty<byte>();
                    return new HeaderReadResult(bytes.ToArray(), tail);
                }
            }
        }
        return new HeaderReadResult(bytes.Length == 0 ? Array.Empty<byte>() : bytes.ToArray(), Array.Empty<byte>());
    }

    private static byte[] AddTokenHeaders(byte[] header, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return header;
        var text = Encoding.Latin1.GetString(header);
        if (text.Contains("\r\n\r\n"))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("tb:" + token));
            text = text.Replace("\r\n\r\n", $"\r\nX-TetherBridge-Token: {token}\r\nProxy-Authorization: Basic {auth}\r\n\r\n");
        }
        return Encoding.Latin1.GetBytes(text);
    }

    private static string FirstLine(byte[] header)
    {
        var text = Encoding.Latin1.GetString(header);
        return text.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    private static string DescribeException(Exception ex) => ex is SocketException socket
        ? $"{ex.GetType().Name}: {ex.Message}, socketError={socket.SocketErrorCode}"
        : $"{ex.GetType().Name}: {ex.Message}";


}
