using System.Net;
using System.Net.Sockets;
using TetherBridge.Desktop.Services;

namespace TetherBridge.Desktop.FullTunnel;

internal static class TunnelDnsProbe
{
    public static async Task CheckAsync(int port, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await Task.WhenAll(
            ProbeDnsAsync(IPAddress.Loopback, port, false, deadline.Token),
            ProbeDnsAsync(IPAddress.Loopback, port, true, deadline.Token),
            ProbeDnsAsync(IPAddress.IPv6Loopback, port, false, deadline.Token),
            ProbeDnsAsync(IPAddress.IPv6Loopback, port, true, deadline.Token));
    }

    private static async Task ProbeDnsAsync(IPAddress loopback, int port, bool tcp, CancellationToken token)
    {
        byte[] query = [0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 0, 1, 0, 1];
        System.Security.Cryptography.RandomNumberGenerator.Fill(query.AsSpan(0, 2));
        byte[] response;
        if (tcp)
        {
            using var socket = new TcpClient(loopback.AddressFamily);
            await socket.ConnectAsync(loopback, port, token);
            var stream = socket.GetStream();
            byte[] length = [0, (byte)query.Length];
            await stream.WriteAsync(length, token);
            await stream.WriteAsync(query, token);
            await stream.ReadExactlyAsync(length, token);
            response = new byte[(length[0] << 8) | length[1]];
            await stream.ReadExactlyAsync(response, token);
        }
        else
        {
            using var socket = new UdpClient(loopback.AddressFamily);
            socket.Connect(loopback, port);
            await socket.SendAsync(query, token);
            response = (await socket.ReceiveAsync(token)).Buffer;
        }
        if (!DnsMessageValidation.HasIpv4Answer(query, response))
            throw new IOException($"Tunnel DNS health check failed on {loopback} over {(tcp ? "TCP" : "UDP")}.");
    }
}
