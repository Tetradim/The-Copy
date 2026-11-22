using System.Buffers.Binary;
namespace TetherBridge.Desktop.Services;

// Validates ordinary single-question DNS messages without following compression pointers.
public static class DnsMessageValidation
{
    public static bool IsQuery(ReadOnlySpan<byte> packet) =>
        packet.Length >= 12 && (packet[2] & 0x80) == 0 &&
        BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2)) == 1 && QuestionEnd(packet) > 0;

    public static byte[] CreateServerFailure(ReadOnlySpan<byte> query)
    {
        if (!IsQuery(query)) throw new ArgumentException("Invalid DNS query.", nameof(query));
        var end = QuestionEnd(query);
        var response = query[..end].ToArray();
        response[2] = (byte)(0x80 | (query[2] & 0x79));
        response[3] = (byte)(0x02 | (query[3] & 0x10));
        response.AsSpan(6, 6).Clear();
        return response;
    }

    public static bool MatchesResponse(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response)
    {
        if (!IsQuery(query) || response.Length < 12 || (response[2] & 0x80) == 0 ||
            query[0] != response[0] || query[1] != response[1] ||
            (query[2] & 0x78) != (response[2] & 0x78) ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2)) != 1) return false;
        var a = QuestionEnd(query); var b = QuestionEnd(response);
        if (a < 0 || b < 0 || a != b) return false;
        for (var i = 12; i < a - 4; i++)
        {
            var x = query[i]; var y = response[i];
            if (x >= 65 && x <= 90) x += 32;
            if (y >= 65 && y <= 90) y += 32;
            if (x != y) return false;
        }
        if (!query.Slice(a - 4, 4).SequenceEqual(response.Slice(b - 4, 4))) return false;
        // A truncated UDP reply must reach the client so it can retry over TCP.
        return (response[2] & 0x02) != 0 || RecordsAreWellFormed(response, b);
    }

    public static bool HasIpv4Answer(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response)
    {
        if (!MatchesResponse(query, response) || (response[2] & 2) != 0 || (response[3] & 15) != 0)
            return false;

        var offset = QuestionEnd(response);
        var count = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2));
        for (var i = 0; i < count; i++)
        {
            // MatchesResponse has already validated the bounds of these records.
            if (!SkipName(response, ref offset)) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 8, 2));
            if (type == 1 && recordClass == 1 && length == 4) return true;
            offset += 10 + length;
        }
        return false;
    }

    private static bool RecordsAreWellFormed(ReadOnlySpan<byte> packet, int offset)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) +
                    BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8, 2)) +
                    BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2));
        for (var i = 0; i < count; i++)
        {
            if (!SkipName(packet, ref offset) || offset + 10 > packet.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 8, 2));
            offset += 10;
            if (offset + length > packet.Length) return false;
            offset += length;
        }
        return offset == packet.Length;
    }

    private static bool SkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var position = offset; var end = -1; var total = 0; var jumps = 0;
        while (position < packet.Length && total <= 255 && jumps <= 128)
        {
            var length = packet[position++];
            if (length == 0) { offset = end < 0 ? position : end; return true; }
            if ((length & 0xc0) == 0xc0)
            {
                if (position >= packet.Length) return false;
                var pointer = ((length & 0x3f) << 8) | packet[position++];
                if (pointer >= position - 2) return false;
                if (end < 0) end = position;
                position = pointer; jumps++; continue;
            }
            if ((length & 0xc0) != 0 || position + length > packet.Length) return false;
            position += length; total += length + 1;
        }
        return false;
    }

    private static int QuestionEnd(ReadOnlySpan<byte> packet)
    {
        var i = 12; var nameLength = 0;
        while (i < packet.Length)
        {
            var length = packet[i++]; nameLength++;
            if (length == 0) return i + 4 <= packet.Length && nameLength <= 255 ? i + 4 : -1;
            if (length > 63 || i + length > packet.Length) return -1;
            i += length; nameLength += length;
            if (nameLength > 255) return -1;
        }
        return -1;
    }
}
