namespace TetherBridge.Desktop.Services;

// Coordinates the existing connection before a new mode starts.
public static class ConnectionTransition
{
    public static async Task<DiagnosticResult> PrepareIfReadyAsync(Func<Task<DiagnosticResult>> check, Func<Task> prepare)
    {
        var result = await check();
        if (result.Ok) await prepare();
        return result;
    }

    public static async Task PrepareAsync(Func<bool> bridgeOwnsProxy, Func<Task> restoreAndStop, Func<Task> stop)
    {
        if (bridgeOwnsProxy()) await restoreAndStop();
        else await stop();
    }
    public static async Task ActivateAsync(Func<Task> activate, Func<Task> rollback)
    {
        try { await activate(); }
        catch (Exception activationError)
        {
            try { await rollback(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Connection activation and rollback failed.", activationError, rollbackError);
            }
            throw;
        }
    }
}
