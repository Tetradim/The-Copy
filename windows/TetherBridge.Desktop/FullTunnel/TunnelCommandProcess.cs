namespace TetherBridge.Desktop.FullTunnel;

internal static class TunnelCommandProcess
{
    public static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        using var job = new ChildProcessJob();
        using var process = job.StartSuspendedThenResume(executable, arguments,
            Environment.SystemDirectory, restrictedEnvironment: true);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Do not begin rollback while the timed-out command can still mutate Windows.
            job.Dispose();
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:g} seconds.");
        }
        return process.ExitCode;
    }
}
