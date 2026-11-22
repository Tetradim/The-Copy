namespace TetherBridge.Desktop.Services;

public static class ProxyStartupRecovery
{
    // A previous record and an exact proxy match suggest recovery, but cannot prove
    // another program has not since chosen the same endpoint. Never mutate here.
    public static bool NeedsReview(ProxyState current, bool listenerExists, bool hasActiveSnapshot) =>
        !listenerExists && hasActiveSnapshot && WindowsProxySettings.IsAppliedLocalProxy(current, "127.0.0.1", 10809);
}
