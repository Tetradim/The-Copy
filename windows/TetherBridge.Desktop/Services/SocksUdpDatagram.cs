using System.Net;
using System.Text;

namespace TetherBridge.Desktop.Services;

public sealed record SocksUdpTarget(string Host, int Port, byte[] AddressAndPort)
{
    public string Authority => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

public static class SocksUdpDatagram
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out SocksUdpTarget? target, out byte[] payload)
    {
        target = null; payload = Array.Empty<byte>();
        if (packet.Length < 4 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return false;
        var addressType = packet[3]; var offset = 4; string host;
        if (addressType == 1 || addressType == 4)
        {
            int size = addressType == 1 ? 4 : 16;
            if (packet.Length < offset + size + 2) return false;
            host = new IPAddress(packet.Slice(offset, size)).ToString(); offset += size;
        }
        else if (addressType == 3)
        {
            if (packet.Length <= offset) return false;
            int size = packet[offset++];
            if (size == 0 || packet.Length < offset + size + 2) return false;
            var name = packet.Slice(offset, size);
            foreach (var b in name) if (b < 33 || b > 126) return false;
            host = Encoding.ASCII.GetString(name);
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) return false;
            offset += size;
        }
        else return false;
        int port = (packet[offset] << 8) | packet[offset + 1];
        if (port == 0) return false;
        offset += 2;
        target = new SocksUdpTarget(host, port, packet.Slice(3, offset - 3).ToArray());
        payload = packet[offset..].ToArray();
        return true;
    }

    public static byte[] BuildReply(SocksUdpTarget target, ReadOnlySpan<byte> payload)
    {
        var result = new byte[3 + target.AddressAndPort.Length + payload.Length];
        target.AddressAndPort.CopyTo(result, 3);
        payload.CopyTo(result.AsSpan(3 + target.AddressAndPort.Length));
        return result;
    }
}
