using System.Text.Json;

namespace TetherBridge.Desktop.Services;

public sealed class ProxyProfileStore
{
    private const int MaxSnapshots = 10;
    private readonly string _dir;
    private readonly Func<bool> _pointsAtBridge;
    private readonly Action<ProxyState> _restore;

    public ProxyProfileStore() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TetherBridge", "proxy-states"),
        () => WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", 10809),
        state => WindowsProxySettings.Restore(state with { AutoDetect = WindowsProxySettings.ReadCurrent().AutoDetect })) { }

    public ProxyProfileStore(string directory, Func<bool> pointsAtBridge, Action<ProxyState> restore)
    {
        _dir = directory;
        _pointsAtBridge = pointsAtBridge;
        _restore = restore;
    }

    private string ActiveMarker => Path.Combine(_dir, "active.snapshot");
    public bool HasActiveSnapshot => File.Exists(ActiveMarker);

    public ProxyState? ReadActiveSnapshot()
    {
        if (!File.Exists(ActiveMarker)) return null;
        var name = File.ReadAllText(ActiveMarker).Trim();
        if (name != Path.GetFileName(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Path.Combine(_dir, name);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<ProxyState>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    public void SaveCurrentBeforeEnable()
    {
        _ = SaveCurrentBeforeEnableIfNeeded("127.0.0.1", 10809, out _);
    }

    public bool SaveCurrentBeforeEnableIfNeeded(string localProxyHost, int localProxyPort, out string message)
    {
        Directory.CreateDirectory(_dir);
        var state = WindowsProxySettings.ReadCurrent();

        // Do not overwrite the user's last real baseline with TetherBridge's own temporary proxy.
        // This matters when users switch USB -> Wi-Fi or retry a connection without restoring first.
        if (state.ProxyEnable == 1 && WindowsProxySettings.ProxyServerContainsEndpoint(state.ProxyServer, localProxyHost, localProxyPort))
        {
            message = "PROXY_SNAPSHOT_SKIPPED: Current proxy already points to TetherBridge; keeping earlier baseline.";
            PruneOldSnapshots();
            return false;
        }

        var path = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        var markerTemp = ActiveMarker + ".tmp";
        File.WriteAllText(markerTemp, Path.GetFileName(path));
        File.Move(markerTemp, ActiveMarker, overwrite: true);
        message = "PROXY_SNAPSHOT_SAVED: Previous Windows proxy state saved to " + Path.GetFileName(path);
        PruneOldSnapshots();
        return true;
    }

    public bool TryRestoreLatest(out string message)
    {
        if (!_pointsAtBridge())
        {
            message = "PROXY_RESTORE_SKIPPED: Windows proxy no longer points to TetherBridge; leaving current settings unchanged.";
            return false;
        }
        Directory.CreateDirectory(_dir);
        string? latest = null;
        if (File.Exists(ActiveMarker))
        {
            var name = File.ReadAllText(ActiveMarker).Trim();
            if (name != Path.GetFileName(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            { message = "RESTORE_CORRUPT: Proxy recovery record is invalid."; return false; }
            latest = Path.Combine(_dir, name);
            if (!File.Exists(latest)) { message = "RESTORE_CORRUPT: Recorded proxy snapshot is missing."; return false; }
        }
        else { message = "RESTORE_NOT_AVAILABLE: No active recovery record was found; historical snapshots were not applied."; return false; }
        if (latest is null)
        {
            message = "RESTORE_NOT_AVAILABLE: No saved proxy state was found.";
            return false;
        }

        ProxyState? state;
        try
        {
            state = JsonSerializer.Deserialize<ProxyState>(File.ReadAllText(latest));
        }
        catch (JsonException)
        {
            message = "RESTORE_CORRUPT: Saved proxy state is invalid and could not be restored.";
            return false;
        }
        if (state is null)
        {
            message = "RESTORE_CORRUPT: Saved proxy state could not be read.";
            return false;
        }

        _restore(state);
        if (File.Exists(ActiveMarker)) File.Delete(ActiveMarker);
        message = "PROXY_RESTORED: Previous Windows proxy state restored from " + Path.GetFileName(latest);
        PruneOldSnapshots();
        return true;
    }

    private void PruneOldSnapshots()
    {
        Directory.CreateDirectory(_dir);
        var stale = Directory.GetFiles(_dir, "*.json")
            .OrderByDescending(x => x)
            .Skip(MaxSnapshots);

        foreach (var path in stale)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
