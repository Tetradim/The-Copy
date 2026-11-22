namespace TetherBridge.Desktop.Services;

// Serializes connection changes and ensures close waits for any active change.
public sealed class ConnectionActionGate
{
    private readonly object state = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closing;

    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (state) if (closing) throw new InvalidOperationException("TetherBridge is closing.");
        await gate.WaitAsync();
        try
        {
            lock (state) if (closing) throw new InvalidOperationException("TetherBridge is closing.");
            await action();
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CloseAsync(Func<Task<bool>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (state) { if (closing) throw new InvalidOperationException("TetherBridge is already closing."); closing = true; }
        await gate.WaitAsync();
        var success = false;
        try { success = await action(); return success; }
        finally { if (!success) lock (state) closing = false; gate.Release(); }
    }
}
