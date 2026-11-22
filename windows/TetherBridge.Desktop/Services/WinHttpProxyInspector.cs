using System.Diagnostics;

namespace TetherBridge.Desktop.Services;

public sealed class WinHttpProxyInspector
{
    public async Task<DiagnosticResult> CheckAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "winhttp show proxy")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return DiagnosticResult.Fail("WINHTTP_UNKNOWN", "Could not start netsh to inspect WinHTTP proxy settings.", "netsh winhttp show proxy", "Run diagnostics from an interactive Windows session.");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return DiagnosticResult.Fail("WINHTTP_CHECK_FAILED", "Could not inspect WinHTTP proxy settings.", "netsh winhttp show proxy", stderr.Trim());

            if (stdout.Contains("Direct access", StringComparison.OrdinalIgnoreCase) || stdout.Contains("no proxy", StringComparison.OrdinalIgnoreCase))
                return DiagnosticResult.Pass("WINHTTP_DIRECT", "WinHTTP is not configured to use a proxy. Proxy Bridge still only covers WinINet/current-user proxy-aware apps.", "netsh winhttp show proxy");

            return DiagnosticResult.Fail("WINHTTP_PROXY_OTHER", "WinHTTP has a separate proxy configuration that TetherBridge is not changing yet.", "netsh winhttp show proxy", "Document this for the user or add an explicit WinHTTP sync/restore option later.");
        }
        catch (Exception ex)
        {
            return DiagnosticResult.Fail("WINHTTP_UNKNOWN", "Could not inspect WinHTTP proxy settings.", "netsh winhttp show proxy", ex.Message);
        }
    }
}
