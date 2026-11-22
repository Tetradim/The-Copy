using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using TetherBridge.Desktop.FullTunnel;

internal static class LabHttpsProbe
{
    public static async Task CheckAsync(Guid adapterId, IPAddress destination, Uri httpsUrl, CancellationToken token)
    {
        if (httpsUrl.Scheme != Uri.UriSchemeHttps || httpsUrl.UserInfo.Length != 0)
            throw new ArgumentException("Supply an HTTPS URL without credentials.");
        var adapter = WindowsPrivateAdapterSnapshot.Read(adapterId) ??
            throw new IOException("The requested adapter was not found.");
        var isIpv4 = destination.AddressFamily == AddressFamily.InterNetwork;
        var expectedIndex = isIpv4 ? adapter.Ipv4Index : adapter.Ipv6Index;
        if (expectedIndex <= 0 || WindowsRouteTable.GetPreferredInterface(destination) != expectedIndex)
            throw new IOException("Windows does not route the destination through the requested adapter.");

        var source = IPAddress.Parse(isIpv4 ? "198.18.0.1" : "fd7a:6272:6964::1");
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
            .SingleOrDefault(item => Guid.TryParse(item.Id, out var id) && id == adapterId);
        if (networkInterface is null ||
            !networkInterface.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(source)))
            throw new IOException("The requested adapter does not own the tunnel source address.");

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellation) =>
            {
                var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(destination, context.DnsEndPoint.Port), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(httpsUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var current = WindowsPrivateAdapterSnapshot.Read(adapterId);
        if (current is null || current.Luid != adapter.Luid ||
            WindowsRouteTable.GetPreferredInterface(destination) != expectedIndex)
            throw new IOException("The selected tunnel route changed during the HTTPS check.");
    }
}
