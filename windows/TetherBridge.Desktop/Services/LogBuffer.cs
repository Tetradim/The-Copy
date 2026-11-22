namespace TetherBridge.Desktop.Services;

// Backward-compatible alias target retained for older notes/tests; MainForm uses RecentLogBuffer.
public sealed class LogBuffer(int maxLines = 1000)
{
    private readonly RecentLogBuffer _inner = new(maxLines);
    public void Add(string line) => _inner.Add(line);
    public string[] Snapshot() => _inner.Snapshot();
    public void Clear() => _inner.Clear();
}
