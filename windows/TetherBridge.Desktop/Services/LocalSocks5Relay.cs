using System.Net;
using System.Net.Sockets;
using System.Text;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop.Services;

public sealed record LocalSocks5RelayStatsSnapshot(
    int ActiveConnections,
    long TotalConnections,
    long BytesClientToPhone,
    long BytesPhoneToClient,
    long UnsupportedUdpRequests)
{
    public string ToDisplayString() =>
        $"SOCKS5_RELAY_STATS: active={ActiveConnections}, total={TotalConnections}, " +
        $"client_to_phone={BytesClientToPhone:N0} bytes, phone_to_client={BytesPhoneToClient:N0} bytes, udp_rejected={UnsupportedUdpRequests}";
}

public sealed class LocalSocks5Relay
{
    private const byte SocksVersion = 0x05;
    private const byte CMD_CONNECT = 0x01;
    private const byte CMD_UDP_ASSOCIATE = 0x03;

    private readonly SemaphoreSlim _connectionLimit = new(128, 128);
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private int _activeConnections;
    private long _totalConnections;
    private long _bytesClientToPhone;
    private long _bytesPhoneToClient;
    private long _unsupportedUdpRequests;

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    public void Start(PhoneBridgeSession session, int localPort, Action<string> log, bool allowUdp = true)
    {
        Stop();
        _activeConnections = 0;
        _totalConnections = 0;
        _bytesClientToPhone = 0;
        _bytesPhoneToClient = 0;
        _unsupportedUdpRequests = 0;
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, localPort) { ExclusiveAddressUse = true };
        try { listener.Start(64); }
        catch { listener.Stop(); cts.Dispose(); throw; }
        _cts = cts;
        _listener = listener;
        var token = cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, session, log, token, allowUdp));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts = null;
        _listener = null;
    }

    public LocalSocks5RelayStatsSnapshot GetSnapshot() => new(
        Volatile.Read(ref _activeConnections),
        Volatile.Read(ref _totalConnections),
        Volatile.Read(ref _bytesClientToPhone),
        Volatile.Read(ref _bytesPhoneToClient),
        Volatile.Read(ref _unsupportedUdpRequests));

    private async Task AcceptLoopAsync(TcpListener listener, PhoneBridgeSession session, Action<string> log, CancellationToken ct, bool allowUdp)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (!_connectionLimit.Wait(0))
                {
                    client.Close();
                    log("LOCAL_SOCKS5_BUSY: Connection limit reached; rejected a local SOCKS5 client.");
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(client, session, log, ct, allowUdp); }
                    finally { _connectionLimit.Release(); }
                });
            }
            catch when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log("LOCAL_SOCKS5_ACCEPT_ERROR: " + ex.Message); }
        }
    }

    private async Task HandleAsync(TcpClient client, PhoneBridgeSession session, Action<string> log, CancellationToken ct, bool allowUdp)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        using var _ = client;
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown-client";
        var target = "no-target";
        try
        {
            var clientStream = client.GetStream();
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));

            var request = await ReadSocksRequestAsync(clientStream, log, handshakeCts.Token);
            if (!request.Ok)
            {
                if (request.UnsupportedUdp) Interlocked.Increment(ref _unsupportedUdpRequests);
                return;
            }
            target = request.Target;
            if (request.Command == CMD_UDP_ASSOCIATE)
            {
                if (!allowUdp)
                {
                    Interlocked.Increment(ref _unsupportedUdpRequests);
                    await SendSocksReplyAsync(clientStream, 0x07, ct);
                    log("PHONE_UDP_UNAVAILABLE: Update the Android app to enable UDP forwarding.");
                    return;
                }
                await SocksUdpAssociation.RunAsync(client, session, request.ClientPort, log,
                    n => Interlocked.Add(ref _bytesClientToPhone, n),
                    n => Interlocked.Add(ref _bytesPhoneToClient, n),
                    () => Interlocked.Increment(ref _unsupportedUdpRequests), ct);
                return;
            }

            await using var phone = await ConnectPhoneAsync(session, ct);
            var connectHeader = BuildHttpConnectRequest(request.Target, session.Token);
            await phone.WriteAsync(connectHeader, ct);
            await phone.FlushAsync(ct);
            Interlocked.Add(ref _bytesClientToPhone, connectHeader.Length);

            var response = await ReadHttpHeaderAsync(phone, handshakeCts.Token);
            if (!IsHttpConnectSuccess(response.Header))
            {
                await SendSocksReplyAsync(clientStream, 0x05, ct);
                log("LOCAL_SOCKS5_PHONE_CONNECT_FAILED: " + FirstLine(response.Header));
                return;
            }

            await SendSocksReplyAsync(clientStream, 0x00, ct);
            if (response.Tail.Length > 0)
            {
                await clientStream.WriteAsync(response.Tail, ct);
                Interlocked.Add(ref _bytesPhoneToClient, response.Tail.Length);
            }

            await DuplexRelay.RunAsync(clientStream, client.Client, phone,
                n => Interlocked.Add(ref _bytesClientToPhone, n), n => Interlocked.Add(ref _bytesPhoneToClient, n), ct);
        }
        catch (Exception ex)
        {
            log($"LOCAL_SOCKS5_CONNECTION_ERROR: client={clientEndpoint}, target=\"{target}\", error={DescribeException(ex)}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task<SocksRequest> ReadSocksRequestAsync(Stream stream, Action<string> log, CancellationToken ct)
    {
        var greeting = await ReadExactAsync(stream, 2, ct);
        if (greeting[0] != SocksVersion) return SocksRequest.Fail("Unsupported SOCKS version.");
        var methodCount = greeting[1];
        if (methodCount == 0) return SocksRequest.Fail("SOCKS5 greeting did not offer authentication methods.");
        var methods = await ReadExactAsync(stream, methodCount, ct);
        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { SocksVersion, 0xFF }, ct);
            return SocksRequest.Fail("No supported SOCKS5 authentication method was offered.");
        }
        await stream.WriteAsync(new byte[] { SocksVersion, 0x00 }, ct);

        var header = await ReadExactAsync(stream, 4, ct);
        if (header[0] != SocksVersion) return SocksRequest.Fail("Unsupported SOCKS request version.");
        if (header[1] != CMD_CONNECT && header[1] != CMD_UDP_ASSOCIATE)
        {
            await SendSocksReplyAsync(stream, 0x07, ct);
            return SocksRequest.Fail("Unsupported SOCKS5 command.");
        }

        var addressType = (AddressType)header[3];
        var host = addressType switch
        {
            AddressType.IPv4 => new IPAddress(await ReadExactAsync(stream, 4, ct)).ToString(),
            AddressType.Domain => Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1, ct))[0], ct)),
            AddressType.IPv6 => new IPAddress(await ReadExactAsync(stream, 16, ct)).ToString(),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            await SendSocksReplyAsync(stream, 0x08, ct);
            return SocksRequest.Fail("Unsupported address type.");
        }

        var portBytes = await ReadExactAsync(stream, 2, ct);
        var port = (portBytes[0] << 8) | portBytes[1];
        if (header[1] == CMD_CONNECT && port == 0)
        {
            await SendSocksReplyAsync(stream, 0x01, ct);
            return SocksRequest.Fail("CONNECT requires a non-zero port.");
        }
        var target = addressType == AddressType.IPv6 ? $"[{host}]:{port}" : $"{host}:{port}";
        return SocksRequest.Success(target, header[1], port);
    }

    private async Task<HeaderReadResult> ReadHttpHeaderAsync(Stream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var readBuffer = new byte[4096];
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;

        while (bytes.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct);
            if (n == 0) break;
            for (var i = 0; i < n; i++)
            {
                var b = readBuffer[i];
                bytes.WriteByte(b);
                matched = b == terminator[matched] ? matched + 1 : b == terminator[0] ? 1 : 0;
                if (matched == terminator.Length)
                {
                    var tail = i + 1 < n ? readBuffer[(i + 1)..n] : Array.Empty<byte>();
                    return new HeaderReadResult(Encoding.ASCII.GetString(bytes.ToArray()), tail);
                }
            }
        }
        return new HeaderReadResult(Encoding.ASCII.GetString(bytes.ToArray()), Array.Empty<byte>());
    }

    private async Task<Stream> ConnectPhoneAsync(PhoneBridgeSession session, CancellationToken ct)
    {
        // Keep one failed client connection local to that client. The session
        // monitor owns whole-session teardown after repeated health failures.
        return await session.Transport.ConnectAsync(ct);
    }

    private static byte[] BuildHttpConnectRequest(string target, string? token)
    {
        var auth = string.IsNullOrWhiteSpace(token)
            ? ""
            : $"X-TetherBridge-Token: {token}\r\nProxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("tb:" + token))}\r\n";
        return Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{auth}Connection: keep-alive\r\n\r\n");
    }

    private static bool IsHttpConnectSuccess(string header) =>
        header.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) ||
        header.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase);

    private static string DescribeException(Exception ex) => ex is SocketException socket
        ? $"{ex.GetType().Name}: {ex.Message}, socketError={socket.SocketErrorCode}"
        : $"{ex.GetType().Name}: {ex.Message}";

    private static async Task SendSocksReplyAsync(Stream stream, byte replyCode, CancellationToken ct)
    {
        var response = new byte[] { SocksVersion, replyCode, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(response, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0) throw new IOException("Unexpected end of stream.");
            offset += n;
        }
        return buffer;
    }



    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault()?.Trim() ?? text;

    private enum AddressType : byte
    {
        IPv4 = 0x01,
        Domain = 0x03,
        IPv6 = 0x04
    }

    private sealed record HeaderReadResult(string Header, byte[] Tail);
    private sealed record SocksRequest(bool Ok, string Target, bool UnsupportedUdp, byte Command, int ClientPort)
    {
        public static SocksRequest Success(string target, byte command, int port) => new(true, target, false, command, port);
        public static SocksRequest Fail(string _, bool unsupportedUdp = false) => new(false, "", unsupportedUdp, 0, 0);
    }
}
