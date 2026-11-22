using TetherBridge.Desktop.Services;

namespace TetherBridge.Desktop.Transports;

public interface IPhoneTransport
{
    string DisplayName { get; }
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
    Task<DiagnosticResult> ProbeHealthAsync(string? token);
    Task<DiagnosticResult> ProbeStatusAsync(string? token);
}
