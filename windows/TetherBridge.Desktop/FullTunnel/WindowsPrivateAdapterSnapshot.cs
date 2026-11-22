using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TetherBridge.Desktop.FullTunnel;

public static class WindowsPrivateAdapterSnapshot
{
    public static PrivateAdapterSnapshot? Read(Guid adapterId)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => Guid.TryParse(item.Id, out var id) && id == adapterId);
        if (adapter is null) return null;
        var result = ConvertInterfaceGuidToLuid(ref adapterId, out var luid);
        if (result != 0) throw new Win32Exception((int)result);
        var properties = adapter.GetIPProperties();
        return new PrivateAdapterSnapshot(adapterId, luid, properties.GetIPv4Properties()?.Index ?? 0,
            properties.GetIPv6Properties()?.Index ?? 0, WindowsRouteTable.Read());
    }
    [DllImport("iphlpapi.dll", SetLastError = false)] private static extern uint ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong interfaceLuid);
}
