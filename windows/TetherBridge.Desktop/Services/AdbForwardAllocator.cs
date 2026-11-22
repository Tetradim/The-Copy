namespace TetherBridge.Desktop.Services;

public static class AdbForwardAllocator
{
    public static bool IsOwnedMapping(string list, string serial, int localPort, int phonePort) =>
        list.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Any(fields => fields.Length == 3 && fields[0] == serial &&
                fields[1] == $"tcp:{localPort}" && fields[2] == $"tcp:{phonePort}");

    public static async Task<(DiagnosticResult Result, int Port)> CreateAsync(int phonePort, Func<string, Task<(int ExitCode, string StdOut, string StdErr)>> run)
    {
        var result = await run($"forward tcp:0 tcp:{phonePort}");
        if (result.ExitCode != 0)
            return (DiagnosticResult.Fail("ADB_FORWARD_FAILED", "USB port forwarding could not be created.", "adb forward tcp:0", result.StdErr.Trim()), 0);
        if (!int.TryParse(result.StdOut.Trim(), out var localPort) || localPort is < 1 or > 65535)
            return (DiagnosticResult.Fail("ADB_FORWARD_BAD_PORT", "ADB did not return a valid local port.", "adb forward tcp:0", "Check the installed Android platform-tools and try again."), 0);
        return (DiagnosticResult.Pass("ADB_FORWARD_OK", $"Forwarded local TCP {localPort} to phone TCP {phonePort}.", "adb forward tcp:0"), localPort);
    }
}
