namespace TetherBridge.Desktop.FullTunnel;

/// <summary>
/// v0.7+ contract sketch for the elevated service.
/// The normal desktop app should not directly create Wintun adapters, edit routes, or edit DNS.
/// </summary>
public interface IFullTunnelServiceContract
{
    Task<FullTunnelStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<FullTunnelStatus> StartAsync(FullTunnelStartRequest request, CancellationToken cancellationToken);
    Task<FullTunnelStatus> StopAsync(CancellationToken cancellationToken);
}

public sealed record FullTunnelStartRequest(
    string RelayHost,
    int RelayPort,
    string? PrimaryInterfaceHint,
    bool EnableKillSwitch);

public sealed record FullTunnelStatus(
    bool Running,
    string State,
    string Message);
