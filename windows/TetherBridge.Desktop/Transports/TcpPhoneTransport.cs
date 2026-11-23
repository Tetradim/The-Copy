using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using TetherBridge.Desktop.Services;

namespace TetherBridge.Desktop.Transports;

public sealed class TcpPhoneTransport(string host, int port, string? fingerprint = null, bool framedUsb = false) : IPhoneTransport
{
    // Android 0.6.8 has 32 framed USB workers. Leave room for health and status
    // checks and for the phone to finish closing recently released connections.
    private readonly SemaphoreSlim? _usbDataSlots = framedUsb ? new(28, 28) : null;
    private readonly SemaphoreSlim _usbControl = new(1, 1);

    public string DisplayName => $"TCP {host}:{port}";

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_usbDataSlots is null) return await OpenAsync(cancellationToken);
        await _usbDataSlots.WaitAsync(cancellationToken);
        try { return await OpenAsync(cancellationToken, () => _usbDataSlots.Release()); }
        catch { _usbDataSlots.Release(); throw; }
    }

    private async Task<Stream> OpenAsync(CancellationToken cancellationToken, Action? release = null)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken);
            if (fingerprint is null) { var raw = new HalfCloseNetworkStream(socket); return framedUsb ? new FramedUsbStream(raw, release) : raw; }
            if (framedUsb) throw new ArgumentException("Wireless TLS and framed USB are separate transports.");
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                throw new ArgumentException("A valid SHA-256 certificate fingerprint is required.");
            var expected = Convert.FromHexString(fingerprint);
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false,
                (_, certificate, _, _) => {
                    var actual = certificate is null ? Array.Empty<byte>() : SHA256.HashData(certificate.GetRawCertData());
                    return certificate is not null && CryptographicOperations.FixedTimeEquals(actual, expected);
                });
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                }, cancellationToken);
                return new TlsPhoneStream(tls);
            }
            catch { await tls.DisposeAsync(); throw; }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<DiagnosticResult> ProbeHealthAsync(string? token)
    {
        var response = await ProbeAsync("/health", token);
        if (response.Exception is not null)
            return DiagnosticResult.Fail(
                "PHONE_PROXY_DOWN",
                $"Phone bridge is not reachable over {DisplayName}. Detail: {DescribeException(response.Exception)}",
                response.ProbeSummary,
                PhoneProbeFix(response));
        if (HasStatus(response.Text, 200))
            return DiagnosticResult.Pass("PHONE_PROXY_OK", $"Phone bridge is reachable over {DisplayName}. {response.ResponseSummary}", response.ProbeSummary);
        if (HasStatus(response.Text, 407))
            return DiagnosticResult.Fail("TOKEN_REJECTED", $"Phone rejected or required the pairing token. {response.ResponseSummary}", response.ProbeSummary, "Re-enter the token shown on Android.");
        return DiagnosticResult.Fail("PHONE_PROXY_BAD_RESPONSE", $"Phone bridge gave an unexpected response. {response.ResponseSummary}", response.ProbeSummary, BadResponseFix(response));
    }

    public async Task<DiagnosticResult> ProbeStatusAsync(string? token)
    {
        var response = await ProbeAsync("/status", token);
        if (response.Exception is not null)
            return DiagnosticResult.Fail(
                "PHONE_STATUS_DOWN",
                $"Phone bridge status is not reachable over {DisplayName}. Detail: {DescribeException(response.Exception)}",
                response.ProbeSummary,
                PhoneProbeFix(response));
        if (HasStatus(response.Text, 200))
            return DiagnosticResult.Pass("PHONE_STATUS_OK", ExtractBody(response.Text), response.ProbeSummary);
        if (HasStatus(response.Text, 407))
            return DiagnosticResult.Fail("TOKEN_REJECTED", $"Phone rejected or required the pairing token for status. {response.ResponseSummary}", response.ProbeSummary, "Re-enter the token shown on Android.");
        return DiagnosticResult.Fail("PHONE_STATUS_BAD_RESPONSE", $"Phone bridge returned an unexpected status response. {response.ResponseSummary}", response.ProbeSummary, BadResponseFix(response));
    }

    private async Task<ProbeResult> ProbeAsync(string path, string? token)
    {
        var sw = Stopwatch.StartNew();
        var bytesRead = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (framedUsb) await _usbControl.WaitAsync(cts.Token);
            try
            {
                var response = await ReadProbeAsync(path, token, cts.Token);
                bytesRead = Encoding.UTF8.GetByteCount(response);
                sw.Stop();
                return new ProbeResult(response, null, ProbeSummary(path, sw.Elapsed, bytesRead), ResponseSummary(response, bytesRead, sw.Elapsed), bytesRead, sw.Elapsed);
            }
            finally
            {
                if (framedUsb) _usbControl.Release();
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(string.Empty, ex, ProbeSummary(path, sw.Elapsed, bytesRead), $"response-bytes={bytesRead}, durationMs={(long)sw.Elapsed.TotalMilliseconds}", bytesRead, sw.Elapsed);
        }
    }

    private async Task<string> ReadProbeAsync(string path, string? token, CancellationToken cancellationToken)
    {
        await using var s = await OpenAsync(cancellationToken);
        var header = $"GET {path} HTTP/1.1\r\nHost: phone\r\n" +
                     (string.IsNullOrWhiteSpace(token) ? "" : $"X-TetherBridge-Token: {token}\r\n") +
                     "Connection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(header);
        await s.WriteAsync(bytes, cancellationToken);
        await s.FlushAsync(cancellationToken);
        return await HttpProbeResponseReader.ReadAsync(s, cancellationToken);
    }

    private static bool HasStatus(string response, int status)
    {
        var line = FirstLine(response);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && (parts[0] == "HTTP/1.1" || parts[0] == "HTTP/1.0") && parts[1] == status.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExtractBody(string response)
    {
        var split = response.Split("\r\n\r\n", 2, StringSplitOptions.None);
        return split.Length == 2 ? split[1].Trim() : FirstLine(response);
    }

    private string ProbeSummary(string path, TimeSpan duration, int bytesRead) =>
        $"TCP {host}:{port} GET {path}, durationMs={(long)duration.TotalMilliseconds}, responseBytes={bytesRead}";

    private static string ResponseSummary(string text, int bytesRead, TimeSpan duration)
    {
        var firstLine = FirstLine(text);
        return $"firstLine=\"{firstLine}\", responseBytes={bytesRead}, durationMs={(long)duration.TotalMilliseconds}";
    }

    private static string ResponseSnippet(string text)
    {
        if (string.IsNullOrEmpty(text)) return "No response bytes received.";
        var normalized = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return normalized.Length <= 220 ? normalized : normalized[..220] + "...";
    }

    private string PhoneProbeFix(ProbeResult response)
    {
        if (framedUsb)
            return "Install the current TetherBridge Android app, open it, tap Start USB Bridge, and confirm USB debugging is authorized. The Windows app creates the ADB forward automatically.";
        if (fingerprint is not null)
            return "Check that the Android Wi-Fi bridge is running and reachable, then pair again using its current certificate fingerprint and token.";
        if (response.Exception is OperationCanceledException || response.ResponseBytes == 0)
            return "ADB forward is present, but the phone bridge did not answer. On Samsung/One UI, open the Android app, allow notifications, open Battery / Unrestricted Settings, set the app to unrestricted, then tap Start USB Bridge again.";
        return "Open the Android app, tap Start USB Bridge, verify adb forward tcp:10888 tcp:10888, then retry.";
    }

    private static string BadResponseFix(ProbeResult response)
    {
        if (response.ResponseBytes == 0)
            return "Phone accepted the TCP connection but returned no HTTP bytes. This matches Android freezing/suspending the bridge; allow notifications, set battery usage to unrestricted, and restart USB Bridge in the Android app.";
        return ResponseSnippet(response.Text);
    }

    private static string DescribeException(Exception ex)
    {
        var socketDetail = ex is SocketException socket ? $", socketError={socket.SocketErrorCode}" : "";
        var inner = ex.InnerException is null ? "" : $", inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        return $"{ex.GetType().Name}: {ex.Message}{socketDetail}{inner}";
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault()?.Trim() ?? text;

    private sealed record ProbeResult(string Text, Exception? Exception, string ProbeSummary, string ResponseSummary, int ResponseBytes, TimeSpan Duration);
}
