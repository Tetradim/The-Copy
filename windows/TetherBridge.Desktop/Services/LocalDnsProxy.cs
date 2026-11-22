using System.Net;
using System.Net.Sockets;

namespace TetherBridge.Desktop.Services;

// Loopback-only DNS listener. Callers provide the resolver and control its lifetime.
public sealed class LocalDnsProxy
{
    private UdpClient? _socket;
    private TcpListener? _tcp;
    private UdpClient? _udp6;
    private TcpListener? _tcp6;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _limit = new(64, 64);
    public int Port => (_socket?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public bool Ipv6Enabled => _udp6 is not null && _tcp6 is not null;
    public bool OwnsSystemDnsPorts => Port == 53 && _tcp is not null && Ipv6Enabled;

    public void Start(Func<byte[], CancellationToken, Task<byte[]>> resolver, int port = 0, Func<byte[], CancellationToken, Task<byte[]>>? tcpResolver = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_socket is not null) throw new InvalidOperationException("DNS proxy is already running.");
        UdpClient? socket = null, udp6 = null;
        TcpListener? tcp = null, tcp6 = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                socket = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
                socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                var boundPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
                tcp = new TcpListener(IPAddress.Loopback, boundPort) { ExclusiveAddressUse = true };
                tcp.Start();
                if (Socket.OSSupportsIPv6)
                {
                    try
                    {
                        udp6 = new UdpClient(AddressFamily.InterNetworkV6) { ExclusiveAddressUse = true };
                        udp6.Client.DualMode = false;
                        udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, boundPort));
                        tcp6 = new TcpListener(IPAddress.IPv6Loopback, boundPort) { ExclusiveAddressUse = true };
                        tcp6.Server.DualMode = false;
                        tcp6.Start();
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
                    { udp6?.Dispose(); tcp6?.Stop(); udp6 = null; tcp6 = null; }
                }
                break;
            }
            catch (SocketException ex) when (port == 0 && attempt < 15 && (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied))
            { socket?.Dispose(); tcp?.Stop(); udp6?.Dispose(); tcp6?.Stop(); socket = null; tcp = null; udp6 = null; tcp6 = null; }
            catch { socket?.Dispose(); tcp?.Stop(); udp6?.Dispose(); tcp6?.Stop(); throw; }
        }
        if (socket is null || tcp is null) throw new IOException("Unable to allocate a DNS listener port.");
        var cts = new CancellationTokenSource();
        _socket = socket; _tcp = tcp; _udp6 = udp6; _tcp6 = tcp6; _cts = cts;
        var token = cts.Token;
        _ = Task.Run(() => ReceiveAsync(socket, resolver, token));
        _ = Task.Run(() => AcceptTcpAsync(tcp, tcpResolver ?? resolver, token));
        if (udp6 is not null) _ = Task.Run(() => ReceiveAsync(udp6, resolver, token));
        if (tcp6 is not null) _ = Task.Run(() => AcceptTcpAsync(tcp6, tcpResolver ?? resolver, token));
    }

    public void Stop()
    {
        var cts = _cts; var socket = _socket; var tcp = _tcp; var udp6 = _udp6; var tcp6 = _tcp6;
        _cts = null; _socket = null; _tcp = null; _udp6 = null; _tcp6 = null;
        cts?.Cancel(); socket?.Dispose(); tcp?.Stop(); udp6?.Dispose(); tcp6?.Stop();
    }

    private async Task AcceptTcpAsync(TcpListener listener, Func<byte[], CancellationToken, Task<byte[]>> resolver, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                if (!_limit.Wait(0)) { client.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    Task<byte[]>? pending = null;
                    using (client)
                    try
                    {
                        var stream = client.GetStream();
                        while (!token.IsCancellationRequested)
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            var length = new byte[2]; await stream.ReadExactlyAsync(length, timeout.Token);
                            var size = (length[0] << 8) | length[1];
                            if (size < 12 || size > 65485) return;
                            var query = new byte[size]; await stream.ReadExactlyAsync(query, timeout.Token);
                            if (!DnsMessageValidation.IsQuery(query)) return;
                            byte[] response;
                            try
                            {
                                pending = Task.Run(() => resolver(query, timeout.Token), CancellationToken.None);
                                response = await pending.WaitAsync(timeout.Token);
                                if (!DnsMessageValidation.MatchesResponse(query, response) || response.Length > 65535) throw new IOException("Invalid DNS response.");
                            }
                            catch (Exception) when (!token.IsCancellationRequested) { response = DnsMessageValidation.CreateServerFailure(query); }
                            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            sendTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                            length[0] = (byte)(response.Length >> 8); length[1] = (byte)response.Length;
                            await stream.WriteAsync(length, sendTimeout.Token); await stream.WriteAsync(response, sendTimeout.Token);
                            if (pending is { IsCompleted: false }) return;
                        }
                    }
                    catch (Exception) { }
                    finally { ReleaseWhenCompleted(pending); }
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }

    // Keep the concurrency permit if an upstream ignores cancellation; otherwise
    // repeated timeouts could spawn an unbounded number of lingering resolver tasks.
    private void ReleaseWhenCompleted(Task? pending)
    {
        if (pending is null) { _limit.Release(); return; }
        _ = pending.ContinueWith(task => { _ = task.Exception; _limit.Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ReceiveAsync(UdpClient socket, Func<byte[], CancellationToken, Task<byte[]>> resolver, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await socket.ReceiveAsync(token);
                if (!DnsMessageValidation.IsQuery(request.Buffer)) continue;
                if (!_limit.Wait(0))
                {
                    try { await socket.SendAsync(DnsMessageValidation.CreateServerFailure(request.Buffer), request.RemoteEndPoint, token); }
                    catch (SocketException) { }
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    Task<byte[]>? pending = null;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        byte[] response;
                        try
                        {
                            pending = Task.Run(() => resolver(request.Buffer, timeout.Token), CancellationToken.None);
                            response = await pending.WaitAsync(timeout.Token);
                            if (!DnsMessageValidation.MatchesResponse(request.Buffer, response) || response.Length > 65507) throw new IOException("Invalid DNS response.");
                        }
                        catch (Exception) when (!token.IsCancellationRequested) { response = DnsMessageValidation.CreateServerFailure(request.Buffer); }
                        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        sendTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await socket.SendAsync(response, request.RemoteEndPoint, sendTimeout.Token);
                    }
                    catch (Exception) { }
                    finally { ReleaseWhenCompleted(pending); }
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }
}
