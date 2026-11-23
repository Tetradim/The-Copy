using System.Net;
using System.Security.Principal;
using TetherBridge.Desktop.FullTunnel;
using TetherBridge.Desktop.Services;
using TetherBridge.Desktop.Transports;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

if (args is ["--benchmark-bridge-usb", var benchmarkSerial, var benchmarkOrigin, var benchmarkSize, var benchmarkWorkers] &&
    Uri.TryCreate(benchmarkOrigin, UriKind.Absolute, out var bridgeOrigin) &&
    int.TryParse(benchmarkSize, out var bridgeSize) && int.TryParse(benchmarkWorkers, out var bridgeWorkers))
{
    try
    {
        await WithUsbRelayAsync(benchmarkSerial, port => LabUsbBenchmark.RunAsync(bridgeOrigin, bridgeSize, bridgeWorkers, port, null));
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--measure-usb-recovery", var recoveryOrigin, var recoveryPort] &&
    Uri.TryCreate(recoveryOrigin, UriKind.Absolute, out var recoveryUrl) &&
    int.TryParse(recoveryPort, out var recoverySocksPort) && recoverySocksPort is > 0 and <= 65535)
{
    try
    {
        await LabUsbBenchmark.MeasureRecoveryAsync(recoveryUrl, recoverySocksPort);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args.Length == 6 && args[0] == "--benchmark-usb" &&
    Uri.TryCreate(args[1], UriKind.Absolute, out var origin) &&
    int.TryParse(args[2], out var mib) && int.TryParse(args[3], out var workers) &&
    int.TryParse(args[5], out var number) &&
    ((args[4] == "direct" && number >= 0) || (args[4] == "socks" && number is > 0 and <= 65535)))
{
    try
    {
        await LabUsbBenchmark.RunAsync(origin, mib, workers,
            args[4] == "socks" ? number : null, args[4] == "direct" && number > 0 ? number : null);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--check-usb-setup"])
{
    Console.WriteLine((await UsbDevicePresence.CheckAsync()).ToDisplayString());
    Console.WriteLine(new NativeTetherInspector().CheckUsbRoute().ToDisplayString());
    return 0;
}

if (args is ["--verify-tunnel-https", var adapter, var remote, var url] &&
    Guid.TryParse(adapter, out var probeAdapter) && IPAddress.TryParse(remote, out var probeRemote) &&
    Uri.TryCreate(url, UriKind.Absolute, out var probeUrl))
{
    try
    {
        await LabHttpsProbe.CheckAsync(probeAdapter, probeRemote, probeUrl, CancellationToken.None);
        Console.WriteLine("HTTPS passed through the selected tunnel adapter with Windows proxy bypassed.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--check-usb-traffic", var usbSerial, var resolverIp, var httpsUrl] &&
    IPAddress.TryParse(resolverIp, out var trafficResolver) &&
    Uri.TryCreate(httpsUrl, UriKind.Absolute, out var trafficUrl) && trafficUrl.Scheme == Uri.UriSchemeHttps && trafficUrl.UserInfo.Length == 0)
{
    try
    {
        await CheckUsbDnsAsync(usbSerial, trafficResolver, trafficUrl);
        Console.WriteLine("HTTPS and DNS passed through the selected USB bridge.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--check-usb-dns", var serial, var target] && IPAddress.TryParse(target, out var usbResolver))
{
    try
    {
        await CheckUsbDnsAsync(serial, usbResolver);
        Console.WriteLine("DNS preflight passed over USB (IPv4 and IPv6 loopback, TCP and UDP).");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--check-preferred-interface", var address] && IPAddress.TryParse(address, out var destination))
{
    try
    {
        var index = WindowsRouteTable.GetPreferredInterface(destination);
        Console.WriteLine($"Windows selected interface {index} for {destination}.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args is ["--check-sharing", var adapterText] && Guid.TryParse(adapterText, out var adapterId))
{
    try
    {
        var profiles = NetworkInformation.GetConnectionProfiles()
            .Where(profile => profile.NetworkAdapter?.NetworkAdapterId == adapterId).ToArray();
        if (profiles.Length != 1)
        {
            Console.Error.WriteLine($"Expected one Windows connection profile for the specified adapter. Found {profiles.Length}.");
            return 1;
        }
        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profiles[0]);
        Console.WriteLine($"Windows Mobile Hotspot capability for the selected upstream: {capability}.");
        return capability == TetheringCapability.Enabled ? 0 : 1;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 2;
    }
}

if (args is ["--capture-state" or "--compare-state", var path])
{
    try
    {
        var current = LabNetworkState.Capture().ToJson();
        if (args[0] == "--capture-state")
        {
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            output.Write(current);
            Console.WriteLine("Captured adapter, DNS and route state.");
            return 0;
        }
        if (File.ReadAllText(path) == current)
        {
            Console.WriteLine("Adapter, DNS and route state matches the baseline.");
            return 0;
        }
        Console.Error.WriteLine("Network state differs from the baseline. Capture the current state to a new file and compare.");
        return 1;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 2;
    }
}

if (args is ["--check-routes"])
{
    try
    {
        await new WindowsFullTunnelRuntime(IPAddress.Loopback).PreflightAsync(CancellationToken.None);
        Console.WriteLine("No existing route conflicts found. Routes and DNS were not changed.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

var shareWifi = args is ["--isolated-network-test", _, "--share-wifi"];
if ((!shareWifi && args.Length != 2) || args[0] != "--isolated-network-test" || !IPAddress.TryParse(args[1], out var resolver))
{
    Console.WriteLine("Check loopback DNS over USB: --check-usb-dns <Android serial> <upstream DNS IP>");
    Console.WriteLine("Check Windows hotspot capability for an exact upstream adapter: --check-sharing <adapter GUID>");
    Console.WriteLine("Record or compare a baseline: --capture-state <file> or --compare-state <file>");
    Console.WriteLine("Check the interface Windows prefers for an IP: --check-preferred-interface <IP>");
    Console.WriteLine("Verify HTTPS without a proxy: --verify-tunnel-https <adapter GUID> <destination IP> <https URL>");
    Console.WriteLine("Measure USB paths: --benchmark-usb <origin> <MiB-per-stream> <streams> <direct|socks> <interface-index|SOCKS-port>");
    Console.WriteLine("Read-only check: TetherBridge.TunnelLab --check-routes");
    Console.WriteLine("Usage: TetherBridge.TunnelLab --isolated-network-test <upstream DNS IP> [--share-wifi]");
    Console.WriteLine("Starts real Windows routing and adapter DNS. Requires a dedicated USB or Bluetooth SOCKS5 relay on 127.0.0.1:10810.");
    return args.Length == 0 ? 0 : 2;
}

using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.Error.WriteLine("The isolated test requires an administrator terminal.");
    return 2;
}

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += cancelHandler;
var runtime = new WindowsFullTunnelRuntime(resolver);
var supervisor = new TunnelSessionSupervisor(runtime);
try
{
    await supervisor.StartAsync(stop.Token);
    Console.WriteLine("Tunnel routes installed. Verify traffic from another terminal. Press Ctrl+C for cleanup.");
    var monitoring = new TunnelLeaseMonitor(supervisor, runtime.CheckHealthAsync, TimeSpan.FromSeconds(2)).RunAsync(stop.Token);
    if (shareWifi)
    {
        try
        {
            await runtime.StartSharingAsync(stop.Token);
            Console.WriteLine("Windows reports the hotspot is on. Join using the existing Mobile Hotspot settings and verify client traffic.");
        }
        catch (Exception startupError)
        {
            stop.Cancel();
            try { await monitoring; }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Sharing startup and tunnel cleanup failed.", startupError, cleanupError);
            }
            throw;
        }
    }
    await monitoring;
    Console.WriteLine("Tunnel cleanup completed.");
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("Tunnel setup canceled and cleanup completed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}


static Task CheckUsbDnsAsync(string serial, IPAddress resolver, Uri? trafficUrl = null) =>
    WithUsbRelayAsync(serial, async socksPort =>
    {
        var dnsRuntime = new WindowsFullTunnelRuntime(resolver);
        try
        {
            await dnsRuntime.StartDnsAsync(socksPort, CancellationToken.None);
            if (trafficUrl is not null)
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                    UseProxy = true,
                    AllowAutoRedirect = false
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await client.GetAsync(trafficUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
            }
        }
        finally { await dnsRuntime.StopEngineAsync(CancellationToken.None); }
    });

static async Task WithUsbRelayAsync(string serial, Func<int, Task> run)
{
    var adb = new AdbService(serial);
    var (forward, port) = await adb.AllocateForwardAsync(10889);
    if (!forward.Ok) throw new IOException(forward.ToDisplayString());
    var relay = new LocalSocks5Relay();
    try
    {
        var transport = new TcpPhoneTransport("127.0.0.1", port, framedUsb: true);
        var health = await transport.ProbeHealthAsync(null);
        if (!health.Ok) throw new IOException(health.ToDisplayString());
        var status = await transport.ProbeStatusAsync(null);
        if (!status.Ok) throw new IOException(status.ToDisplayString());
        relay.Start(new PhoneBridgeSession(transport, null), 0, _ => { },
            allowUdp: PhoneCapabilities.SupportsUdp(status.Message));
        await run(relay.Port);
    }
    finally
    {
        relay.Stop();
        var released = await adb.ReleaseOwnedForwardAsync();
        if (!released.Ok) throw new IOException(released.ToDisplayString());
    }
}
