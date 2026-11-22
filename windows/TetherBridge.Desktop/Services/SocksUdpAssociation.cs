using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop.Services;

/// <summary>A loopback-only SOCKS5 UDP association, scoped to its TCP control connection.</summary>
public static class SocksUdpAssociation
{
    private const int MaxFlows = 32;
    private const long IdleMilliseconds = 60000;

    public static async Task RunAsync(TcpClient control, PhoneBridgeSession session, int requestedPort, Action<string> log, Action<int> countUp, Action<int> countDown, Action reject, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bind = (IPEndPoint)udp.Client.LocalEndPoint!;
        var reply = new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(bind.Port >> 8), (byte)bind.Port };
        var stream = control.GetStream();
        await stream.WriteAsync(reply, cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var flows = new Dictionary<string, UdpFlow>();
        IPEndPoint? clientEndpoint = null;
        var controlAddress = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;

        async Task ReceiveDatagrams()
        {
            while (!cts.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(cts.Token);
                if (!received.RemoteEndPoint.Address.Equals(controlAddress) ||
                    (requestedPort != 0 && received.RemoteEndPoint.Port != requestedPort)) { reject(); continue; }
                if (clientEndpoint is not null && !clientEndpoint.Equals(received.RemoteEndPoint)) { reject(); continue; }
                if (!SocksUdpDatagram.TryParse(received.Buffer, out var target, out var payload) || target is null) { reject(); continue; }
                clientEndpoint ??= received.RemoteEndPoint;
                var now = Environment.TickCount64;
                foreach (var expired in flows.Where(x => x.Value.IsClosed || now - x.Value.LastActivity > IdleMilliseconds).Select(x => x.Key).ToArray())
                {
                    var old = flows[expired]; flows.Remove(expired); await old.DisposeAsync();
                }
                var key = Convert.ToHexString(target.AddressAndPort);
                if (!flows.TryGetValue(key, out var flow))
                {
                    if (flows.Count >= MaxFlows) { reject(); log("SOCKS_UDP_FLOW_LIMIT: Too many destinations in one UDP association."); continue; }
                    try
                    {
                        var peer = clientEndpoint;
                        flow = await UdpFlow.OpenAsync(session, target, async data =>
                        {
                            var packet = SocksUdpDatagram.BuildReply(target, data);
                            if (packet.Length <= 65507) { await udp.SendAsync(packet, peer, cts.Token); countDown(data.Length); }
                            else reject();
                        }, cts.Token);
                        flows.Add(key, flow);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                    catch (Exception ex) { log("SOCKS_UDP_CONNECT_FAILED: " + ex.Message); continue; }
                }
                try { await flow.SendAsync(payload, cts.Token); countUp(payload.Length); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) { log("SOCKS_UDP_SEND_FAILED: " + ex.Message); flows.Remove(key); await flow.DisposeAsync(); }
            }
        }

        async Task WatchControl()
        {
            var buffer = new byte[1];
            while (await stream.ReadAsync(buffer, cts.Token) != 0) { }
        }

        var receiveTask = ReceiveDatagrams();
        var controlTask = WatchControl();
        try { await Task.WhenAny(receiveTask, controlTask); }
        finally
        {
            cts.Cancel();
            try { await Task.WhenAll(receiveTask, controlTask); } catch (OperationCanceledException) { } catch (IOException) { } catch (SocketException) { }
            foreach (var flow in flows.Values) await flow.DisposeAsync();
        }
    }

    private sealed class UdpFlow : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly Func<byte[], Task> _sendReply;
        private readonly CancellationTokenSource _cts;
        private readonly Task _reader;
        private int _closed;
        private long _lastActivity;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;
        public long LastActivity => Interlocked.Read(ref _lastActivity);

        private UdpFlow(Stream stream, Func<byte[], Task> sendReply, CancellationToken token)
        {
            _stream = stream; _sendReply = sendReply;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            _reader = ReadRepliesAsync();
        }

        public static async Task<UdpFlow> OpenAsync(PhoneBridgeSession session, SocksUdpTarget target, Func<byte[], Task> sendReply, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = await session.Transport.ConnectAsync(timeout.Token);
            try
            {
                var auth = string.IsNullOrWhiteSpace(session.Token) ? "" : $"X-TetherBridge-Token: {session.Token}\r\n";
                var header = Encoding.ASCII.GetBytes($"CONNECT-UDP {target.Authority} HTTP/1.1\r\nHost: {target.Authority}\r\n{auth}Connection: keep-alive\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                var response = new MemoryStream(); var one = new byte[1]; var end = new byte[] { 13, 10, 13, 10 }; var match = 0;
                while (response.Length < 8192 && match < end.Length)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token); response.WriteByte(one[0]);
                    match = one[0] == end[match] ? match + 1 : one[0] == end[0] ? 1 : 0;
                }
                var text = Encoding.ASCII.GetString(response.ToArray());
                if (match != end.Length || !(text.StartsWith("HTTP/1.1 200 ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("HTTP/1.0 200 ", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Android did not accept the UDP tunnel. Update both apps and start the bridge.");
                return new UdpFlow(stream, sendReply, token);
            }
            catch { await stream.DisposeAsync(); throw; }
        }

        public async Task SendAsync(byte[] data, CancellationToken token)
        {
            if (data.Length > 65507) throw new IOException("UDP datagram is too large.");
            var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            await _stream.WriteAsync(header, token); await _stream.WriteAsync(data, token); await _stream.FlushAsync(token);
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        }

        private async Task ReadRepliesAsync()
        {
            try
            {
                var header = new byte[4];
                while (!_cts.IsCancellationRequested)
                {
                    await _stream.ReadExactlyAsync(header, _cts.Token);
                    var size = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (size < 0 || size > 65507) throw new IOException("Invalid UDP frame length.");
                    var data = new byte[size]; await _stream.ReadExactlyAsync(data, _cts.Token);
                    await _sendReply(data);
                    Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
                }
            }
            catch (Exception) { }
            finally { Interlocked.Exchange(ref _closed, 1); }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); await _stream.DisposeAsync();
            try { await _reader; } catch (Exception) { }
            _cts.Dispose();
        }
    }
}
