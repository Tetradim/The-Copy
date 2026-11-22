using System.Text.Json;
namespace TetherBridge.Desktop.Services;

public sealed record DesktopSettings(bool AutoConnectUsb = false);
public sealed class DesktopSettingsStore(string path)
{
    public DesktopSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
