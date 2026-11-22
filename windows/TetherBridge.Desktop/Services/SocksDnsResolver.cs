using System.Net;
using System.Net.Sockets;

namespace TetherBridge.Desktop.Services;

public sealed class SocksDnsResolver(int proxyPort, IPAddress upstream)
{
    public async Task<byte[]> ResolveAsync(byte[] query, CancellationToken token)
    {
        if (!DnsMessageValidation.IsQuery(query)) throw new ArgumentException("Invalid DNS query.");
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, proxyPort, token);
        var stream = control.GetStream();
        await stream.WriteAsync(new byte[] {5, 1, 0}, token);
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
        if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS authentication failed.");
        var localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await stream.WriteAsync(new byte[] {5,3,0,1,127,0,0,1,(byte)(localPort >> 8),(byte)localPort}, token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, token);
        if (reply[0] != 5 || reply[1] != 0 || reply[3] != 1) throw new IOException("SOCKS UDP association failed.");
        var relayAddress = new IPAddress(reply.AsSpan(4,4));
        if (!IPAddress.IsLoopback(relayAddress)) throw new IOException("SOCKS UDP relay is not loopback.");
        var relay = new IPEndPoint(relayAddress, (reply[8] << 8) | reply[9]);
        var ip = upstream.GetAddressBytes();
        var address = new byte[1 + ip.Length + 2]; address[0] = ip.Length == 4 ? (byte)1 : (byte)4;
        ip.CopyTo(address, 1); address[^2] = 0; address[^1] = 53;
        var target = new SocksUdpTarget(upstream.ToString(), 53, address);
        await udp.SendAsync(SocksUdpDatagram.BuildReply(target, query), relay, token);
        while (true)
        {
            var packet = await udp.ReceiveAsync(token);
            if (!packet.RemoteEndPoint.Equals(relay) || !SocksUdpDatagram.TryParse(packet.Buffer, out var source, out var response) || source is null) continue;
            if (!source.AddressAndPort.SequenceEqual(address) || !DnsMessageValidation.MatchesResponse(query, response)) continue;
            return response;
        }
    }
    public async Task<byte[]> ResolveTcpAsync(byte[] query, CancellationToken token)
    {
        if (!DnsMessageValidation.IsQuery(query) || query.Length > 65535) throw new ArgumentException("Invalid DNS query.");
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, proxyPort, token);
        var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token);
        if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS authentication failed.");
        var ip = upstream.GetAddressBytes();
        var request = new byte[4 + ip.Length + 2];
        request[0] = 5; request[1] = 1; request[3] = ip.Length == 4 ? (byte)1 : (byte)4;
        ip.CopyTo(request, 4); request[^1] = 53;
        await stream.WriteAsync(request, token);
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        if (header[0] != 5 || header[1] != 0 || header[2] != 0) throw new IOException("SOCKS DNS connection failed.");
        int addressLength = header[3] switch { 1 => 4, 4 => 16, 3 => await ReadLengthAsync(stream, token), _ => throw new IOException("Invalid SOCKS address type.") };
        var boundAddress = new byte[addressLength + 2]; await stream.ReadExactlyAsync(boundAddress, token);
        await stream.WriteAsync(new byte[] { (byte)(query.Length >> 8), (byte)query.Length }, token);
        await stream.WriteAsync(query, token);
        var length = new byte[2]; await stream.ReadExactlyAsync(length, token);
        var response = new byte[(length[0] << 8) | length[1]];
        await stream.ReadExactlyAsync(response, token);
        if (!DnsMessageValidation.MatchesResponse(query, response)) throw new IOException("Mismatched DNS response.");
        return response;
    }

    private static async Task<int> ReadLengthAsync(NetworkStream stream, CancellationToken token)
    {
        var length = new byte[1]; await stream.ReadExactlyAsync(length, token); return length[0];
    }

}
