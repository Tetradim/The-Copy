namespace TetherBridge.Desktop.Services;

public sealed class RecentLogBuffer(int maxLines = 1000)
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > maxLines) _lines.Dequeue();
        }
    }

    public string[] Snapshot(int? takeLast = null)
    {
        lock (_gate)
        {
            var copy = _lines.ToArray();
            return takeLast is { } n && n > 0 ? copy.TakeLast(n).ToArray() : copy;
        }
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }

    public static string Redact(string text, string? token) => SupportBundleExporter.Redact(text, token);
}
