using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace TetherBridge.Desktop.FullTunnel;

// Experimental: Windows controls a single system hotspot. Use in an isolated test environment.
internal sealed class WindowsTunnelWifiShare
{
    private NetworkOperatorTetheringManager? startedManager;

    public static ConnectionProfile FindUpstream(Guid adapterId)
    {
        var profiles = NetworkInformation.GetConnectionProfiles()
            .Where(profile => profile.NetworkAdapter?.NetworkAdapterId == adapterId).ToArray();
        if (profiles.Length != 1)
            throw new IOException($"Expected one Windows connection profile for the tunnel adapter. Found {profiles.Length}.");
        return profiles[0];
    }

    public async Task StartAsync(Guid adapterId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (startedManager is not null) throw new InvalidOperationException("The lab hotspot is already running.");
        var profile = FindUpstream(adapterId);
        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
        if (capability != TetheringCapability.Enabled)
            throw new IOException($"Windows Mobile Hotspot is unavailable for the tunnel adapter ({capability}).");
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        if (manager.TetheringOperationalState != TetheringOperationalState.Off)
            throw new IOException("Windows Mobile Hotspot must be off before starting the lab hotspot.");
        token.ThrowIfCancellationRequested();

        // Let Windows return the outcome even on Ctrl+C, so cleanup can stop a successful start.
        var result = await manager.StartTetheringAsync();
        if (result.Status != TetheringOperationStatus.Success)
            throw new IOException($"Windows could not share the tunnel adapter ({result.Status}).");
        startedManager = manager;
        if (manager.TetheringOperationalState != TetheringOperationalState.On)
            throw new IOException("Windows reported a successful hotspot start but did not show it running.");
    }

    public async Task StopAsync()
    {
        var manager = startedManager;
        if (manager is null) return;
        if (manager.TetheringOperationalState != TetheringOperationalState.Off)
        {
            var result = await manager.StopTetheringAsync();
            if (result.Status != TetheringOperationStatus.Success || manager.TetheringOperationalState != TetheringOperationalState.Off)
                throw new IOException($"Windows did not confirm that the lab hotspot stopped ({result.Status}).");
        }
        startedManager = null;
    }
}
