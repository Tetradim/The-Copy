using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace TetherBridge.Desktop.FullTunnel;

public sealed record WindowsRoute(ulong InterfaceLuid, int InterfaceIndex, string Prefix, IPAddress NextHop, uint Metric, uint ValidLifetime, bool Immortal);

// Read-only route enumeration via the Windows IP Helper API. No routes are changed.
public static class WindowsRouteTable
{
    public static IReadOnlyList<WindowsRoute> Read()
    {
        var result = GetIpForwardTable2(0, out var table); // AF_UNSPEC
        if (result != 0) throw new Win32Exception((int)result);
        try
        {
            var count = Marshal.ReadInt32(table);
            if (count < 0 || count > 100000) throw new InvalidDataException("Unexpected route table size.");
            var offset = (int)Marshal.OffsetOf<TableLayout>(nameof(TableLayout.First));
            var rowSize = Marshal.SizeOf<RouteRow>();
            var routes = new List<WindowsRoute>(count);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<RouteRow>(IntPtr.Add(table, checked(offset + i * rowSize)));
                var destination = Address(row.Destination.Address);
                var nextHop = Address(row.NextHop);
                if (destination is null || nextHop is null) continue;
                routes.Add(new WindowsRoute(row.InterfaceLuid, checked((int)row.InterfaceIndex),
                    $"{destination}/{row.Destination.PrefixLength}", nextHop, row.Metric, row.ValidLifetime, row.Immortal != 0));
            }
            return routes;
        }
        finally { FreeMibTable(table); }
    }
    public static int GetPreferredInterface(IPAddress destination)
    {
        var bytes = destination.GetAddressBytes();
        var address = bytes.Length == 4
            ? new SockaddrInet { Family = 2, Ipv4 = BitConverter.ToUInt32(bytes, 0) }
            : new SockaddrInet
            {
                Family = 23,
                V6A = BitConverter.ToUInt32(bytes, 0), V6B = BitConverter.ToUInt32(bytes, 4),
                V6C = BitConverter.ToUInt32(bytes, 8), V6D = BitConverter.ToUInt32(bytes, 12),
                Scope = checked((uint)destination.ScopeId)
            };
        var result = GetBestInterfaceEx(ref address, out var index);
        if (result != 0) throw new Win32Exception((int)result);
        return checked((int)index);
    }

    private static IPAddress? Address(SockaddrInet value)
    {
        if (value.Family == 2) return new IPAddress(BitConverter.GetBytes(value.Ipv4));
        if (value.Family == 23)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(value.V6A).CopyTo(bytes, 0); BitConverter.GetBytes(value.V6B).CopyTo(bytes, 4);
            BitConverter.GetBytes(value.V6C).CopyTo(bytes, 8); BitConverter.GetBytes(value.V6D).CopyTo(bytes, 12);
            return new IPAddress(bytes);
        }
        return null;
    }
    [StructLayout(LayoutKind.Explicit, Size = 28)] private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Ipv4;
        [FieldOffset(8)] public uint V6A;
        [FieldOffset(12)] public uint V6B;
        [FieldOffset(16)] public uint V6C;
        [FieldOffset(20)] public uint V6D;
        [FieldOffset(24)] public uint Scope;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AddressPrefix { public SockaddrInet Address; public byte PrefixLength; }
    [StructLayout(LayoutKind.Sequential)] private struct RouteRow
    {
        public ulong InterfaceLuid; public uint InterfaceIndex;
        public AddressPrefix Destination; public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime, PreferredLifetime, Metric, Protocol;
        public byte Loopback, AutoconfigureAddress, Publish, Immortal;
        public uint Age, Origin;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TableLayout { public uint Count; public RouteRow First; }
    [DllImport("iphlpapi.dll", SetLastError = false)] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll", SetLastError = false)] private static extern uint GetBestInterfaceEx(ref SockaddrInet address, out uint index);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);
}
