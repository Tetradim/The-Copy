using System.Text;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop.Services;

public static class SupportBundleExporter
{
    public static async Task<string> BuildAsync(
        IEnumerable<string> logLines,
        LocalRelayStatsSnapshot relaySnapshot,
        LocalSocks5RelayStatsSnapshot socksSnapshot,
        PhoneBridgeSession? session,
        IEnumerable<DiagnosticResult> diagnostics,
        IEnumerable<DiagnosticResult>? adbEvidence = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TetherBridge Classic support bundle");
        sb.AppendLine("Generated: " + DateTimeOffset.Now.ToString("O"));
        sb.AppendLine("Version: 0.6.7");
        sb.AppendLine();
        sb.AppendLine("Local relay:");
        sb.AppendLine(relaySnapshot.ToDisplayString());
        sb.AppendLine();
        sb.AppendLine("SOCKS5 relay:");
        sb.AppendLine(socksSnapshot.ToDisplayString());
        sb.AppendLine();
        sb.AppendLine("Phone status:");
        if (session is null)
        {
            sb.AppendLine("No active phone session.");
        }
        else
        {
            var status = await session.Transport.ProbeStatusAsync(session.Token);
            sb.AppendLine(Redact(status.ToDisplayString(), session.Token));
        }
        sb.AppendLine();
        sb.AppendLine("Diagnostics:");
        foreach (var d in diagnostics) sb.AppendLine(Redact(d.ToDisplayString(), session?.Token));
        sb.AppendLine();
        sb.AppendLine("ADB evidence:");
        if (adbEvidence is null)
        {
            sb.AppendLine("No ADB evidence collected.");
        }
        else
        {
            foreach (var d in adbEvidence) sb.AppendLine(Redact(d.ToDisplayString(), session?.Token));
        }
        sb.AppendLine();
        sb.AppendLine("Recent logs:");
        foreach (var line in logLines) sb.AppendLine(Redact(line, session?.Token));
        return sb.ToString();
    }

    public static string Redact(string text, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) text = text.Replace(token, "<redacted-token>", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"token=([^&\s]+)", "token=<redacted-token>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"X-TetherBridge-Token:\s*\S+", "X-TetherBridge-Token: <redacted-token>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"Proxy-Authorization:\s*\S+\s+\S+", "Proxy-Authorization: <redacted-token>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }
}
