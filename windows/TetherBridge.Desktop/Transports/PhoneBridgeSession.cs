using TetherBridge.Desktop.Services;

namespace TetherBridge.Desktop.Transports;

public sealed record PhoneBridgeSession(
    IPhoneTransport Transport,
    string? Token,
    Func<Task<DiagnosticResult>>? ReconnectAsync = null)
{
    public async Task<DiagnosticResult?> TryReconnectAsync()
    {
        if (ReconnectAsync is null) return null;
        return await ReconnectAsync();
    }
}
