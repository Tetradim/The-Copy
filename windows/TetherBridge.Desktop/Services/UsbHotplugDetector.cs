namespace TetherBridge.Desktop.Services;

public sealed class UsbHotplugDetector
{
    private HashSet<string>? previous;
    public bool IsInitialized => previous is not null;
    public string[] Observe(IEnumerable<AdbDevice> devices)
    {
        var ready = devices.Where(device => device.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .Select(device => device.Serial).ToHashSet(StringComparer.Ordinal);
        var added = previous is null ? Array.Empty<string>() : ready.Except(previous, StringComparer.Ordinal).ToArray();
        previous = ready;
        return added;
    }
}
