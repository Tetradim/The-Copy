using System.Net.Security;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TetherBridge.Desktop.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace TetherBridge.Desktop.Transports;

public sealed record BluetoothPeer(string Id, string Name) { public override string ToString() => Name; }

public sealed class BluetoothRfcommTransport(string deviceId, string fingerprint) : IPhoneTransport
{
    private const int DataSlotCount = 6;
    private sealed class PeerSlots
    {
        public Channel<int> Data { get; } = CreateDataSlots();
        public SemaphoreSlim Health { get; } = new(1, 1);
        public SemaphoreSlim Status { get; } = new(1, 1);
        private static Channel<int> CreateDataSlots()
        {
            var channel = Channel.CreateBounded<int>(DataSlotCount);
            for (var i = 0; i < DataSlotCount; i++) channel.Writer.TryWrite(i);
            return channel;
        }
    }
    private static readonly ConcurrentDictionary<string, PeerSlots> Slots = new(StringComparer.OrdinalIgnoreCase);
    private static Guid ServiceId(int slot) => Guid.Parse($"953d7f10-3df7-4a8c-a0e7-85744b080d1{8 + slot:x}");
    public string DisplayName => "Bluetooth RFCOMM";

    public static async Task<IReadOnlyList<BluetoothPeer>> ListPairedAsync()
    {
        var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        return found.Select(item => new BluetoothPeer(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Unnamed Bluetooth device" : item.Name)).ToArray();
    }

    public Task<Stream> ConnectAsync(CancellationToken token) =>
        BluetoothConnectionRecovery.OpenWithDeadlineAsync(
            deadline => ConnectCoreAsync(deadline, null), TimeSpan.FromSeconds(15), token);

    private async Task<Stream> ConnectCoreAsync(CancellationToken token, int? controlSlot)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("A valid certificate fingerprint is required.");
        var expected = Convert.FromHexString(fingerprint);
        var slots = Slots.GetOrAdd(deviceId, _ => new PeerSlots());
        int slot;
        Action release;
        if (controlSlot.HasValue)
        {
            slot = controlSlot.Value;
            var gate = slot == 6 ? slots.Health : slots.Status;
            await gate.WaitAsync(token);
            release = () => gate.Release();
        }
        else
        {
            slot = await slots.Data.Reader.ReadAsync(token);
            release = () => slots.Data.Writer.TryWrite(slot);
        }
        var ownershipTransferred = false;
        BluetoothDevice? device = null; RfcommDeviceService? service = null; StreamSocket? socket = null;
        try
        {
            device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(token) ?? throw new IOException("Bluetooth device is unavailable.");
            var services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(ServiceId(slot)), BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Error != BluetoothError.Success || services.Services.Count == 0) throw new IOException("TetherBridge Bluetooth service was not found. Install the matching updated Android app and start Bluetooth Bridge on the paired device.");
            service = services.Services[0];
            foreach (var unused in services.Services.Skip(1)) unused.Dispose();
            socket = new StreamSocket();
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionWithAuthentication).AsTask(token);
            var raw = new RfcommStream(socket, service, device, release);
            ownershipTransferred = true; socket = null; service = null; device = null;
            var tls = new SslStream(raw, false, (_, certificate, _, _) => {
                var actual = certificate is null ? Array.Empty<byte>() : SHA256.HashData(certificate.GetRawCertData());
                return certificate is not null && CryptographicOperations.FixedTimeEquals(actual, expected);
            });
            try {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "tetherbridge", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck }, token);
                return new FramedUsbStream(new TlsPhoneStream(tls));
            } catch { await tls.DisposeAsync(); throw; }
        }
        catch
        {
            // A failure disposing one WinRT object must not prevent the slot from being returned.
            try { socket?.Dispose(); } catch { }
            try { service?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            if (!ownershipTransferred) release();
            throw;
        }
    }

    public Task<DiagnosticResult> ProbeHealthAsync(string? token) => ProbeHealthAsync(token, CancellationToken.None);
    public Task<DiagnosticResult> ProbeHealthAsync(string? token, CancellationToken cancellationToken) =>
        ProbeAsync("/health", token, cancellationToken);
    public Task<DiagnosticResult> ProbeStatusAsync(string? token) => ProbeAsync("/status", token);
    private async Task<DiagnosticResult> ProbeAsync(string path, string? token, CancellationToken cancellationToken = default)
    {
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var stream = await ConnectCoreAsync(timeout.Token, path == "/health" ? 6 : 7);
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: phone\r\nX-TetherBridge-Token: {token}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token); await stream.FlushAsync(timeout.Token);
            var response = await HttpProbeResponseReader.ReadAsync(stream, timeout.Token);
            if (!response.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)) return DiagnosticResult.Fail("BLUETOOTH_PHONE_BAD_RESPONSE", "Bluetooth bridge rejected the request.", "Bluetooth probe", "Check pairing token and restart the Android bridge.");
            var offset = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return DiagnosticResult.Pass(path == "/status" ? "PHONE_STATUS_OK" : "PHONE_PROXY_OK", offset >= 0 ? response[(offset + 4)..] : "Bluetooth bridge is reachable.", "Bluetooth probe");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return DiagnosticResult.Fail("BLUETOOTH_PHONE_DOWN", $"Bluetooth bridge is unavailable: {ex.Message}", "Bluetooth probe", "Pair the devices and start Bluetooth Bridge on Android."); }
    }

    private sealed class RfcommStream(StreamSocket socket, RfcommDeviceService service, BluetoothDevice device, Action release) : Stream
    {
        private int disposed;
        private readonly Stream input = socket.InputStream.AsStreamForRead();
        private readonly Stream output = socket.OutputStream.AsStreamForWrite();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush(); public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) { output.Write(buffer, offset, count); output.Flush(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await output.WriteAsync(buffer, token);
            await output.FlushAsync(token);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try
                {
                    try { input.Dispose(); } catch { }
                    try { output.Dispose(); } catch { }
                    try { socket.Dispose(); } catch { }
                    try { service.Dispose(); } catch { }
                    try { device.Dispose(); } catch { }
                }
                finally { release(); }
            }
            base.Dispose(disposing);
        }
    }
}
