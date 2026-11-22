using System.Net;
using System.Net.Sockets;
using System.Text;
using TetherBridge.Desktop.Services;
using TetherBridge.Desktop.Transports;
using TetherBridge.Desktop.FullTunnel;

if (args.Length == 2 && args[0] == "usb-lab-relay")
{
    var adb = new AdbService(args[1]);
    var (forward, usbPort) = await adb.AllocateForwardAsync(10889);
    if (!forward.Ok) throw new IOException(forward.ToDisplayString());
    var labRelay = new LocalSocks5Relay();
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var transport = new TcpPhoneTransport("127.0.0.1", usbPort, framedUsb: true);
        var status = await transport.ProbeStatusAsync(null);
        if (!status.Ok || !PhoneCapabilities.SupportsUdp(status.Message))
            throw new IOException("Start the compatible Android USB Bridge before running the lab.");
        labRelay.Start(new PhoneBridgeSession(transport, null), 10810, Console.WriteLine);
        Console.WriteLine("USB SOCKS relay ready on 127.0.0.1:10810. Press Ctrl+C after stopping the tunnel lab.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
        labRelay.Stop();
        var released = await adb.ReleaseOwnedForwardAsync();
        if (!released.Ok) throw new IOException(released.ToDisplayString());
    }
    return;
}
if (args is ["tunnel-command-test"])
{
    var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
    var status = await TunnelCommandProcess.RunAsync(netsh, ["interface", "ipv4", "show", "interfaces"],
        TimeSpan.FromSeconds(5), CancellationToken.None);
    if (status != 0) throw new Exception($"Read-only netsh command failed ({status}).");

    var helper = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    var sleepArgs = new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" };
    try
    {
        await TunnelCommandProcess.RunAsync(helper, sleepArgs, TimeSpan.FromMilliseconds(250),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        throw new Exception("Timed-out process was reported as successful.");
    }
    catch (TimeoutException error) when (error.Message.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)) { }

    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    try
    {
        await TunnelCommandProcess.RunAsync(helper, sleepArgs, TimeSpan.FromSeconds(10),
            cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
        throw new Exception("Canceled process was reported as successful.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    Console.WriteLine("PASS: read-only netsh, command timeout and command cancellation");
    return;
}
if (args is ["relay-startup-rollback-test"])
{
    using var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
    occupied.Start();
    var busyPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
    var session = new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1", busyPort), null);
    var http = new LocalRelayProxy();
    var socks = new LocalSocks5Relay();
    try
    {
        await ConnectionTransition.ActivateAsync(() =>
        {
            http.Start(session, 0, _ => { });
            if (!http.IsRunning) throw new Exception("HTTP relay did not start.");
            socks.Start(session, busyPort, _ => { });
            return Task.CompletedTask;
        }, () =>
        {
            http.Stop();
            socks.Stop();
            return Task.CompletedTask;
        });
        throw new Exception("Activation should fail when a required port is taken.");
    }
    catch (SocketException) { }
    if (http.IsRunning || socks.Port != 0)
        throw new Exception("Activation failure left a relay running.");
    Console.WriteLine("PASS: an occupied SOCKS port rolls back partial relay startup");
    return;
}
if (args is ["tunnel-dns-preflight-test"])
{
    var dnsProbe = new LocalDnsProxy();
    var fail = false;
    Task<byte[]> Resolve(byte[] query, CancellationToken token)
    {
        var answer = query.Concat(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 1 }).ToArray();
        answer[2] |= 0x80;
        answer[7] = 1;
        if (fail) answer[3] = 2; // SERVFAIL with a matching ID and question
        return Task.FromResult(answer);
    }
    dnsProbe.Start(Resolve);
    try
    {
        if (!dnsProbe.Ipv6Enabled) throw new Exception("This check requires IPv6 loopback.");
        await TunnelDnsProbe.CheckAsync(dnsProbe.Port, CancellationToken.None);
        fail = true;
        try
        {
            await TunnelDnsProbe.CheckAsync(dnsProbe.Port, CancellationToken.None);
            throw new Exception("DNS preflight accepted SERVFAIL.");
        }
        catch (IOException error) when (error.Message.Contains("DNS health check failed")) { }
    }
    finally { dnsProbe.Stop(); }
    Console.WriteLine("PASS: tunnel DNS preflight checks both loopbacks over TCP and UDP and rejects SERVFAIL");
    return;
}
if (args.Length > 0 && args[0] == "job-crash-host") {
    using var job=new ChildProcessJob();
    var exe=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    using var child=job.StartSuspendedThenResume(exe,new[]{"-NoProfile","-Command","Start-Sleep -Seconds 30"},Environment.SystemDirectory);
    Console.WriteLine(child.Id);Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);return;
}
if (args.Length > 0 && args[0] == "dns-port53") {
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var dns=new LocalDnsProxy();dns.Start((q,_)=>{var a=(byte[])q.Clone();a[2]|=0x80;return Task.FromResult(a);},53);
    try { if(!dns.OwnsSystemDnsPorts)throw new Exception("Both system DNS loopback listeners are required");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        foreach(var endpoint in new[]{new IPEndPoint(IPAddress.Loopback,53),new IPEndPoint(IPAddress.IPv6Loopback,53)}) {
            using var udp=new UdpClient(endpoint.AddressFamily);await udp.SendAsync(query,endpoint,timeout.Token);
            var answer=await udp.ReceiveAsync(timeout.Token);if(!DnsMessageValidation.MatchesResponse(query,answer.Buffer))throw new Exception("System DNS loopback test failed");
        }
        Console.WriteLine("PASS: exclusive IPv4/IPv6 loopback DNS listeners bind and answer on port 53");
    }finally{dns.Stop();}
    return;
}
if (args.Length > 0 && args[0] == "live") {
    var session = new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1", 19088), null);
    var http = new LocalRelayProxy(); var socks = new LocalSocks5Relay();
    http.Start(session, 19089, Console.WriteLine); socks.Start(session, 19090, Console.WriteLine);
    Console.WriteLine("READY");
    try { await Task.Delay(TimeSpan.FromSeconds(45)); } finally { http.Stop(); socks.Stop(); }
    return;
}
if (args.Length > 0 && args[0] == "adb-live") {
    var adb = new AdbService();
    var (result, allocatedPort) = await adb.AllocateForwardAsync(10888);
    if (!result.Ok || allocatedPort == 0) throw new Exception(result.ToDisplayString());
    Console.WriteLine("Allocated test forward on port " + allocatedPort);
    var released = await adb.ReleaseOwnedForwardAsync();
    if (!released.Ok || released.Code != "ADB_FORWARD_REMOVED") throw new Exception(released.ToDisplayString());
    Console.WriteLine("PASS: allocated test forward was removed");
    return;
}
if (args.Length == 2 && args[0] == "tls-device") {
    var devicePort = int.Parse(args[1]);
    var devicePin = Environment.GetEnvironmentVariable("TB_TEST_PIN") ?? throw new Exception("Missing test fingerprint");
    var token = Environment.GetEnvironmentVariable("TB_TEST_TOKEN") ?? throw new Exception("Missing test token");
    var transport = new TcpPhoneTransport("127.0.0.1", devicePort, devicePin);
    var health = await transport.ProbeHealthAsync(token);
    if (!health.Ok) throw new Exception("Pinned device health failed: " + health.Code + " " + health.Message);
    var wrongPin = new TcpPhoneTransport("127.0.0.1", devicePort, new string('0',64));
    if ((await wrongPin.ProbeHealthAsync(token)).Ok) throw new Exception("Device accepted an incorrect certificate devicePin");
    if ((await transport.ProbeHealthAsync("invalid-token")).Code != "TOKEN_REJECTED") throw new Exception("Android did not reject an invalid token");
    using (var raw = new TcpClient()) {
        await raw.ConnectAsync(IPAddress.Loopback, devicePort);
        var rawStream = raw.GetStream();
        await rawStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var bytes = new byte[128];
        try { var n = await rawStream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
              if (System.Text.Encoding.ASCII.GetString(bytes, 0, n).Contains("200 OK")) throw new Exception("Plaintext access was accepted"); }
        catch (IOException) { }
    }
    Console.WriteLine("PASS: Android requires TLS, the paired fingerprint, and a valid token");
    var temp = new TcpListener(IPAddress.Loopback, 0); temp.Start();
    var deviceRelayPort = ((IPEndPoint)temp.LocalEndpoint).Port; temp.Stop();
    var deviceRelay = new LocalRelayProxy(); deviceRelay.Start(new PhoneBridgeSession(transport, token), deviceRelayPort, Console.WriteLine);
    try {
        using var handler = new System.Net.Http.HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + deviceRelayPort), UseProxy = true };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync("https://example.com/");
        if (!response.IsSuccessStatusCode) throw new Exception("HTTPS through Android TLS bridge failed: " + response.StatusCode);
        Console.WriteLine("PASS: HTTPS flows through the encrypted Android bridge");
    } finally { deviceRelay.Stop(); }
    temp = new TcpListener(IPAddress.Loopback, 0); temp.Start();
    var deviceSocksPort = ((IPEndPoint)temp.LocalEndpoint).Port; temp.Stop();
    var deviceSocks = new LocalSocks5Relay(); deviceSocks.Start(new PhoneBridgeSession(transport, token), deviceSocksPort, Console.WriteLine);
    try {
        using var handler = new System.Net.Http.HttpClientHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + deviceSocksPort), UseProxy = true };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync("https://example.com/");
        if (!response.IsSuccessStatusCode) throw new Exception("HTTPS through Android TLS SOCKS bridge failed: " + response.StatusCode);
        Console.WriteLine("PASS: SOCKS HTTPS flows through the encrypted Android bridge");
    } finally { deviceSocks.Stop(); }
    return;
}
if (args.Length == 2 && args[0] == "framed-device") {
    var frameDevicePort = int.Parse(args[1]);
    var frameTransport = new TcpPhoneTransport("127.0.0.1", frameDevicePort, framedUsb: true);
    var frameHealth = await frameTransport.ProbeHealthAsync(null);
    if (!frameHealth.Ok) throw new Exception("Framed USB health failed: " + frameHealth.Message);
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
    var localPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var frameRelay = new LocalRelayProxy(); frameRelay.Start(new PhoneBridgeSession(frameTransport, null), localPort, Console.WriteLine);
    try {
        using var handler = new System.Net.Http.HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + localPort), UseProxy = true };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync("https://example.com/");
        if (!response.IsSuccessStatusCode) throw new Exception("HTTPS through framed USB failed: " + response.StatusCode);
        Console.WriteLine("PASS: HTTPS works through Windows framed USB relay");
        using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, localPort);
        var stream = socket.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:19121 HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var terminator = new byte[] {13,10,13,10}; var matched = 0; var one = new byte[1];
        while (matched < 4) { await stream.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); matched = one[0] == terminator[matched] ? matched + 1 : 0; }
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("request")); socket.Client.Shutdown(SocketShutdown.Send);
        using var output = new MemoryStream(); await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(10));
        if (System.Text.Encoding.ASCII.GetString(output.ToArray()) != "response-after-eof") throw new Exception("Framed USB lost the response after half-close");
        Console.WriteLine("PASS: Windows and Android framed USB preserve a response after half-close");
    } finally { frameRelay.Stop(); }
    reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
    var socksPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var frameSocks = new LocalSocks5Relay(); frameSocks.Start(new PhoneBridgeSession(frameTransport, null), socksPort, Console.WriteLine);
    try {
        using var handler = new System.Net.Http.HttpClientHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + socksPort), UseProxy = true };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync("https://example.com/");
        if (!response.IsSuccessStatusCode) throw new Exception("SOCKS HTTPS through framed USB failed: " + response.StatusCode);
        Console.WriteLine("PASS: SOCKS HTTPS works through Windows framed USB relay");
    } finally { frameSocks.Stop(); }
    return;
}
if (args.Length > 0 && args[0] == "selected-adb-live") {
    var (listing, devices) = await new AdbService().ListDevicesAsync();
    if (!listing.Ok) throw new Exception("Could not list Android devices");
    var tablet = devices.SingleOrDefault(d => d.Model.Equals("SM X218U", StringComparison.OrdinalIgnoreCase));
    if (tablet is null) throw new Exception("Samsung test tablet was not found");
    var chosen = new AdbService(tablet.Serial);
    if (!(await chosen.CheckAsync()).Ok || !(await chosen.QueryPackageVersionAsync()).Ok) throw new Exception("Selected tablet or app was not recognized");
    var liveSelectedAllocation = await chosen.AllocateForwardAsync(10889);
    if (!liveSelectedAllocation.Result.Ok) throw new Exception("Could not allocate selected tablet forward");
    try { Console.WriteLine("PASS: selected Samsung tablet and matching app were recognized"); }
    finally { if (!(await chosen.ReleaseOwnedForwardAsync()).Ok) throw new Exception("Could not remove selected-device forward"); }
    Console.WriteLine("PASS: selected tablet forward was removed");
    return;
}
if (args.Length == 2 && args[0] == "udp-device") {
    var remotePort = int.Parse(args[1]);
    var udpPin = Environment.GetEnvironmentVariable("TB_TEST_PIN");
    var udpToken = Environment.GetEnvironmentVariable("TB_TEST_TOKEN");
    var udpTransport = new TcpPhoneTransport("127.0.0.1", remotePort, udpPin, framedUsb: udpPin is null);
    var actualUdpStatus = await udpTransport.ProbeStatusAsync(udpToken);
    if (!actualUdpStatus.Ok || !PhoneCapabilities.SupportsUdp(actualUdpStatus.Message)) throw new Exception("Android did not advertise UDP support");
    var reserve = new TcpListener(IPAddress.Loopback,0); reserve.Start(); var proxyPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var udpRelay = new LocalSocks5Relay(); udpRelay.Start(new PhoneBridgeSession(udpTransport,udpToken),proxyPort,Console.WriteLine);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try {
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback,proxyPort,timeout.Token); var tcp=control.GetStream();
        await tcp.WriteAsync(new byte[] {5,1,0},timeout.Token); var auth=new byte[2]; await tcp.ReadExactlyAsync(auth,timeout.Token);
        if (auth[1]!=0) throw new Exception("SOCKS authentication failed");
        await tcp.WriteAsync(new byte[] {5,3,0,1,0,0,0,0,0,0},timeout.Token); var reply=new byte[10]; await tcp.ReadExactlyAsync(reply,timeout.Token);
        if (reply[1]!=0) throw new Exception("SOCKS UDP ASSOCIATE was rejected: " + reply[1]);
        var endpoint=new IPEndPoint(new IPAddress(reply.AsSpan(4,4)),(reply[8]<<8)|reply[9]);
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        foreach (var payload in new[] {System.Text.Encoding.ASCII.GetBytes("hello-udp"),Enumerable.Range(0,40000).Select(i=>(byte)i).ToArray(),Array.Empty<byte>()}) {
            var header=new byte[] {0,0,0,1,127,0,0,1,(byte)(19122>>8),(byte)(19122&255)};
            var packet=header.Concat(payload).ToArray(); await udp.SendAsync(packet,endpoint,timeout.Token);
            var received=await udp.ReceiveAsync(timeout.Token);
            if (!received.Buffer.SequenceEqual(packet)) throw new Exception("SOCKS UDP echo mismatch");
        }
        Console.WriteLine("PASS: SOCKS UDP echoes small, 40000-byte and empty datagrams through Windows, ADB and Android");
        if (Environment.GetEnvironmentVariable("TB_TEST_UDP_DNS") == "1") {
            var dnsQuery = new byte[] {0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
            var dnsPacket = new byte[] {0,0,0,1,1,1,1,1,0,53}.Concat(dnsQuery).ToArray();
            await udp.SendAsync(dnsPacket,endpoint,timeout.Token);
            using var dnsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try {
                var dnsReply = await udp.ReceiveAsync(dnsTimeout.Token);
                if (!SocksUdpDatagram.TryParse(dnsReply.Buffer,out _,out var dnsBody) || dnsBody.Length < 12 || dnsBody[0] != 0x4a || dnsBody[1] != 0x42 || (dnsBody[2] & 0x80) == 0) throw new Exception("Invalid DNS response through UDP bridge");
                Console.WriteLine("PASS: DNS query received a response through the UDP bridge");
            } catch (OperationCanceledException) { Console.WriteLine("DNS UDP probe timed out; the external DNS server may be unreachable from this carrier"); }
        }
    } finally { udpRelay.Stop(); }
    return;
}
if (args.Length == 2 && args[0] == "dns-device") {
    var adbPort = int.Parse(args[1]); var transport = new TcpPhoneTransport("127.0.0.1",adbPort,framedUsb:true);
    var reserve = new TcpListener(IPAddress.Loopback,0); reserve.Start(); var socksPort=((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var dnsLiveRelay=new LocalSocks5Relay(); dnsLiveRelay.Start(new PhoneBridgeSession(transport,null),socksPort,_=>{});
    var local=new LocalDnsProxy(); var liveResolver = new SocksDnsResolver(socksPort,IPAddress.Parse("1.1.1.1")); local.Start(liveResolver.ResolveAsync, tcpResolver: liveResolver.ResolveTcpAsync);
    try {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await udp.SendAsync(query,new IPEndPoint(IPAddress.Loopback,local.Port),timeout.Token);
        var answer=await udp.ReceiveAsync(timeout.Token); if(!DnsMessageValidation.HasIpv4Answer(query,answer.Buffer)) throw new Exception("DNS UDP did not return a successful complete answer");
        using var tcp=new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback,local.Port,timeout.Token); var stream=tcp.GetStream();
        var length=new byte[]{(byte)(query.Length>>8),(byte)query.Length}; await stream.WriteAsync(length,timeout.Token);await stream.WriteAsync(query,timeout.Token);
        await stream.ReadExactlyAsync(length,timeout.Token);var body=new byte[(length[0]<<8)|length[1]];await stream.ReadExactlyAsync(body,timeout.Token);
        if(!DnsMessageValidation.HasIpv4Answer(query,body)) throw new Exception("DNS TCP did not return a successful complete answer");
        if (local.Ipv6Enabled) {
            using var udp6=new UdpClient(AddressFamily.InterNetworkV6);
            await udp6.SendAsync(query,new IPEndPoint(IPAddress.IPv6Loopback,local.Port),timeout.Token);
            var answer6=await udp6.ReceiveAsync(timeout.Token);
            if(!DnsMessageValidation.HasIpv4Answer(query,answer6.Buffer)) throw new Exception("IPv6 loopback DNS request failed");
        }
        Console.WriteLine("PASS: local UDP and TCP DNS requests resolve through Samsung USB bridge");
    } finally {local.Stop();dnsLiveRelay.Stop();}
    return;
}
{
    var q = new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var broken = (byte[])q.Clone(); broken[2] = 0x81; broken[3] = 0x80; broken[7] = 1;
    if (DnsMessageValidation.HasIpv4Answer(q, broken)) throw new Exception("DNS accepted missing answer record");
    var valid = broken.Concat(new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,1,2,3,4}).ToArray();
    if (!DnsMessageValidation.HasIpv4Answer(q, valid) || DnsMessageValidation.HasIpv4Answer(q, valid[..^1])) throw new Exception("DNS record framing check failed");
    var unrelatedType = (byte[])valid.Clone();
    unrelatedType[q.Length + 3] = 16; // TXT rather than A
    if (DnsMessageValidation.HasIpv4Answer(q, unrelatedType)) throw new Exception("DNS accepted a non-address answer.");
    Console.WriteLine("PASS: DNS live probe rejects missing and truncated answer records");
}
{
    var applied = new ProxyState(1, "http=127.0.0.1:10809;https=127.0.0.1:10809", "<local>", null, 0);
    if (!WindowsProxySettings.IsAppliedLocalProxy(applied, "127.0.0.1", 10809)) throw new Exception("Applied proxy not recognized");
    if (WindowsProxySettings.IsAppliedLocalProxy(applied with { ProxyServer = "http=127.0.0.1:10809;https=other:443" }, "127.0.0.1", 10809) ||
        WindowsProxySettings.IsAppliedLocalProxy(applied with { ProxyOverride = "other" }, "127.0.0.1", 10809) ||
        WindowsProxySettings.IsAppliedLocalProxy(applied with { AutoConfigUrl = "https://example.test/proxy.pac" }, "127.0.0.1", 10809) ||
        WindowsProxySettings.ProxyServerContainsEndpoint("127.0.0.1:108090", "127.0.0.1", 10809))
        throw new Exception("Changed or unrelated proxy incorrectly treated as TetherBridge-owned");
    Console.WriteLine("PASS: proxy ownership rejects changed settings and port prefix matches");
}
{
    var dir = Path.Combine(Path.GetTempPath(), "tb-net-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
        var backend = new FakeNetworkBackend();
        var txn = new NetworkTransaction(Path.Combine(dir, "journal.json"), backend);
        var changes = new[] { new NetworkChange("route", "default", "old", "tunnel"), new NetworkChange("dns", "adapter", "old-dns", "new-dns") };
        backend.Values["default"] = "old"; backend.Values["adapter"] = "old-dns"; backend.FailAfterWriteKey = "adapter";
        try { await txn.ApplyAsync(changes, CancellationToken.None); throw new Exception("Expected simulated network failure"); }
        catch (IOException) { }
        if (backend.Values["default"] != "old" || backend.Values["adapter"] != "old-dns" || File.Exists(Path.Combine(dir, "journal.json")))
            throw new Exception("Write-ahead rollback did not restore both applied and uncertain changes");
        backend.FailAfterWriteKey = null;
        await txn.ApplyAsync(changes, CancellationToken.None);
        backend.Values["adapter"] = "external";
        if (await txn.RecoverAsync(CancellationToken.None)) throw new Exception("External change should leave recovery unresolved");
        if (backend.Values["adapter"] != "external" || backend.Values["default"] != "tunnel") throw new Exception("Recovery crossed a conflict barrier or clobbered an external change");
        backend.Values["adapter"] = "new-dns";
        if (!await txn.RecoverAsync(CancellationToken.None) || backend.Values["adapter"] != "old-dns" || File.Exists(Path.Combine(dir, "journal.json")))
            throw new Exception("Recovery did not resume safely");
        Console.WriteLine("PASS: write-ahead network recovery handles uncertain writes, conflicts and retry");
    } finally { Directory.Delete(dir, true); }
}

{
    var dir = Path.Combine(Path.GetTempPath(), "tb-conflict-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    try {
        var backend = new FakeNetworkBackend { ConflictAfterWriteKey = "dns" }; backend.Values["route"] = "old"; backend.Values["dns"] = "old";
        var txn = new NetworkTransaction(Path.Combine(dir, "journal.json"), backend);
        var pending = false;
        try { await txn.ApplyAsync(new[] {new NetworkChange("route", "route", "old", "new"), new NetworkChange("dns", "dns", "old", "new")}, CancellationToken.None); }
        catch (NetworkRecoveryPendingException ex) { pending = ex.InnerException is IOException; }
        if (!pending || backend.Values["dns"] != "external" || backend.Values["route"] != "new" || !File.Exists(Path.Combine(dir, "journal.json")))
            throw new Exception("Interrupted activation with conflicting recovery must explicitly report pending recovery and preserve the journal");
        backend.Values["dns"] = "new";
        if (!await txn.RecoverAsync(CancellationToken.None) || backend.Values["route"] != "old") throw new Exception("Pending recovery did not resume");
        Console.WriteLine("PASS: activation reports unresolved conflicting rollback and can retry");
    } finally { Directory.Delete(dir, true); }
}


{
    var dir = Path.Combine(Path.GetTempPath(), "tb-journal-invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    try {
        var path = Path.Combine(dir, "journal.json");
        var change = new NetworkChange("route", "same", "old", "new");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Changes = new[] { change, change } }));
        var backend = new FakeNetworkBackend(); backend.Values["same"] = "new";
        var rejected = false; try { await new NetworkTransaction(path, backend).RecoverAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        if (!rejected || backend.Values["same"] != "new" || !File.Exists(path)) throw new Exception("Duplicate journal entries must be rejected before recovery mutates state");
        Console.WriteLine("PASS: duplicate recovery journal identities rejected before mutation");
    } finally { Directory.Delete(dir, true); }
}

{
    var dir = Path.Combine(Path.GetTempPath(), "tb-lock-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    try {
        var backend = new FakeNetworkBackend(); backend.Values["route"] = "old";
        backend.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(dir, "journal.json");
        var first = new NetworkTransaction(path, backend); var second = new NetworkTransaction(path, backend);
        var applying = first.ApplyAsync(new[] {new NetworkChange("route", "route", "old", "new")}, CancellationToken.None);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = false; try { await second.RecoverAsync(CancellationToken.None); } catch (IOException) { rejected = true; }
        backend.Block.SetResult(); await applying;
        if (!rejected || backend.Values["route"] != "new") throw new Exception("A second transaction entered while the first held the journal lock");
        await first.RecoverAsync(CancellationToken.None);
        Console.WriteLine("PASS: independent network transactions cannot overlap");
    } finally { Directory.Delete(dir, true); }
}
{
    var owned = new ProxyState(1, "http=127.0.0.1:10809;https=127.0.0.1:10809", "<local>", null, 0);
    if (ProxyStartupRecovery.NeedsReview(owned, true, true) || ProxyStartupRecovery.NeedsReview(owned, false, false) ||
        ProxyStartupRecovery.NeedsReview(owned with { ProxyOverride = "external" }, false, true) ||
        !ProxyStartupRecovery.NeedsReview(owned, false, true))
        throw new Exception("Startup review ignored active listener, ownership or recovery record");
    Console.WriteLine("PASS: ambiguous startup proxy is not automatically overwritten");
}
{
    var body = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}");
    using var input = new BlockingAfterContentStream(body);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var response = await HttpProbeResponseReader.ReadAsync(input, timeout.Token);
    if (!response.EndsWith("{}", StringComparison.Ordinal)) throw new Exception("HTTP probe did not stop at Content-Length");
    Console.WriteLine("PASS: phone probe completes on framed response even when peer stays open");
}
// Exercise the actual TCP probe against a server that keeps the socket open.
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var healthTestPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var releaseFirstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = Task.Run(async () => {
        for (int i = 0; i < 2; i++) {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream(); var request = new byte[2048]; await stream.ReadAsync(request);
            var response = i == 0 ? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK" : "HTTP/1.1 500 Error\r\nContent-Length: 6\r\n\r\n200 OK";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            if (i == 0) await releaseFirstProbe.Task;
        }
    });
    var transport = new TcpPhoneTransport("127.0.0.1", healthTestPort);
    if (!(await transport.ProbeHealthAsync(null)).Ok) throw new Exception("TCP health probe waited for socket close");
    releaseFirstProbe.SetResult();
    if ((await transport.ProbeHealthAsync(null)).Ok) throw new Exception("TCP health probe accepted success text in an error body");
    await server;
    Console.WriteLine("PASS: TCP health probe respects response framing and status line");
}

{
    var dir=Path.Combine(Path.GetTempPath(), "tb-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
    try { var store=new DesktopSettingsStore(Path.Combine(dir,"settings.json"));
        if(store.Load().AutoConnectUsb)throw new Exception("Auto-connect must default off");
        store.Save(new DesktopSettings(true)); if(!store.Load().AutoConnectUsb)throw new Exception("Auto-connect preference did not persist");
        File.WriteAllText(Path.Combine(dir,"settings.json"),"{invalid"); if(store.Load().AutoConnectUsb)throw new Exception("Corrupt settings enabled auto-connect");
        Console.WriteLine("PASS: desktop auto-connect is opt-in and fails safe on corrupt settings");
    } finally {Directory.Delete(dir,true);}
}
{
    var gate=new ConnectionActionGate();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var events=new List<string>();
    var connect=gate.RunAsync(async()=>{events.Add("connect-start");entered.SetResult();await release.Task;events.Add("connect-end");});
    await entered.Task;var closing=gate.CloseAsync(async()=>{events.Add("restore");await Task.CompletedTask;return true;});
    var rejected=false;try{await gate.RunAsync(()=>Task.CompletedTask);}catch(InvalidOperationException){rejected=true;}
    if(!rejected || closing.IsCompleted) throw new Exception("Close failed to wait for in-flight connection or admit prevention failed");
    release.SetResult();await connect;await closing;
    if(string.Join(",",events)!="connect-start,connect-end,restore")throw new Exception("Restore ran before in-flight connection finished");
    Console.WriteLine("PASS: close waits for connection and rejects new actions");
}
{
    var lifecycle=new CloseLifecycle();
    if(lifecycle.RequestClose(false)!=CloseAction.BeginCleanup || lifecycle.RequestClose(false)!=CloseAction.WaitForCleanup)
        throw new Exception("Repeated close bypassed in-flight cleanup");
    lifecycle.Complete(false);
    if(lifecycle.RequestClose(false)!=CloseAction.BeginCleanup)throw new Exception("Failed cleanup did not allow retry");
    lifecycle.Complete(true);
    if(lifecycle.RequestClose(false)!=CloseAction.AllowClose)throw new Exception("Completed cleanup did not permit close");
    Console.WriteLine("PASS: repeated close waits until cleanup is approved");
}
{
    var detector=new UsbHotplugDetector();
    var connected=new[]{new AdbDevice("one","device","One")};
    if(detector.Observe(connected).Length!=0)throw new Exception("Initial scan must establish baseline");
    if(detector.Observe(connected).Length!=0)throw new Exception("Existing device triggered reconnect");
    detector.Observe(Array.Empty<AdbDevice>());
    detector.Observe(new[]{new AdbDevice("one","unauthorized","One")});
    if(!detector.Observe(connected).SequenceEqual(new[]{"one"}))throw new Exception("Newly authorized plugged device not detected");
    if(detector.Observe(connected).Length!=0)throw new Exception("Plug-in event repeated");
    Console.WriteLine("PASS: USB hot-plug detects newly ready devices without repeated reconnects");
}
{
    var limiter=new BandwidthLimiter(4096);var watch=System.Diagnostics.Stopwatch.StartNew();
    await limiter.WaitAsync(1024,CancellationToken.None);
    if(watch.Elapsed < TimeSpan.FromMilliseconds(170))throw new Exception("Bandwidth limiter did not delay traffic");
    using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    var canceled=false;try {await limiter.WaitAsync(8192,cancel.Token);}catch(OperationCanceledException){canceled=true;}
    if(!canceled)throw new Exception("Bandwidth limit wait ignored cancellation");
    Console.WriteLine("PASS: optional bandwidth limiter paces traffic and honors cancellation");
}
{
    var limiter=new BandwidthLimiter(1024);
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var large=limiter.WaitAsync(1024,stop.Token);
    await Task.Delay(25,stop.Token);
    var watch=System.Diagnostics.Stopwatch.StartNew();
    await limiter.WaitAsync(1,stop.Token);
    if(watch.Elapsed > TimeSpan.FromMilliseconds(500))throw new Exception("Large transfer starved a small transfer");
    await large;
    Console.WriteLine("PASS: rate limiter shares low bandwidth among concurrent transfers");
}
{
    var raw=new TrackedReadStream(new byte[4096]);
    var transport=new BandwidthLimitedTransport(new SingleStreamTransport(raw),1024);
    await using var limited=await transport.ConnectAsync(CancellationToken.None);
    var buffer=new byte[4096]; using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var count=await limited.ReadAsync(buffer,deadline.Token);
    if(count>103 || count<=0)throw new Exception("Rate-limited reads did not deliver small increments");
    using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
    var canceled=false;try{await limited.ReadAsync(buffer,cancel.Token);}catch(OperationCanceledException){canceled=true;}
    if(!canceled || !raw.WasDisposed)throw new Exception("Canceled paced read left a consumed stream reusable");
    Console.WriteLine("PASS: paced reads make incremental progress and close after cancellation");
}
{
    var add=TunnelRouteCommands.AddIpv4(42,"0.0.0.0/1");
    if(!add.Contains("store=active") || !add.Contains("validlifetime=15") || !add.Contains("42") || !add.Contains("0.0.0.0/1"))throw new Exception("Tunnel route is not a short-lived active-store route");
    var rejected=false;try{TunnelRouteCommands.AddIpv4(42,"10.0.0.0/8");}catch(ArgumentException){rejected=true;}
    if(!rejected)throw new Exception("Tunnel route accepted an unexpected prefix");
    Console.WriteLine("PASS: tunnel route commands restrict prefixes and use expiring active routes");
}
{
    var runner=new FakeRouteRunner { FailCall=3 };
    var manager=new RouteLeaseManager(runner);
    try { await manager.StartAsync(42,true,CancellationToken.None,77);throw new Exception("Expected route failure"); } catch(IOException) { }
    var deletes=runner.Calls.Where(call=>call.Contains("delete")).ToArray();
    if(deletes.Length!=3 || !deletes[0].Contains("77") || !deletes[0].Contains("::/1") || !deletes[2].Contains("0.0.0.0/1"))
        throw new Exception("Partial route activation was not unwound in reverse order, including the uncertain operation");
    Console.WriteLine("PASS: failed route activation unwinds attempted private-adapter routes");
}

{
    var time = new FakeMonotonicClock();
    var runner = new FakeRouteRunner { AfterMutation = args => { if (args[2] == "add") time.Advance(TimeSpan.FromSeconds(6)); } };
    var manager = new RouteLeaseManager(runner, time);
    var rejected = false; try { await manager.StartAsync(42, false, CancellationToken.None); } catch (IOException) { rejected = true; }
    if (!rejected || runner.Calls.Count(call => call[2] == "delete") != 2) throw new Exception("Slow route activation must reject and clean up an aging lease");
    Console.WriteLine("PASS: slow route activation cannot expose an expired lease");
}


{
    var time = new FakeMonotonicClock(); var runner = new FakeRouteRunner();
    var manager = new RouteLeaseManager(runner, time);
    await manager.StartAsync(42, false, CancellationToken.None);
    time.Advance(TimeSpan.FromSeconds(8));
    runner.AfterMutation = args => { if (args[2] == "set") time.Advance(TimeSpan.FromSeconds(3)); };
    var rejected = false; try { await manager.RenewAsync(CancellationToken.None); } catch (IOException) { rejected = true; }
    if (!rejected || runner.Calls.Count(call => call[2] == "delete") != 2) throw new Exception("Slow renewal must detect the old lease deadline and clean up");
    Console.WriteLine("PASS: slow route renewal respects the earliest old lease deadline");
}

{
    var runner = new FakeRouteRunner { IgnoreAdd = "128.0.0.0/1" };
    var manager = new RouteLeaseManager(runner);
    try
    {
        await manager.StartAsync(42, false, CancellationToken.None);
        throw new Exception("Activation succeeded without all expected routes.");
    }
    catch (IOException) { }
    if (!runner.Calls.Any(call => call[2] == "delete" && call[4] == "0.0.0.0/1"))
        throw new Exception("Incomplete activation did not remove the installed route.");
    Console.WriteLine("PASS: inactive route after reported success triggers rollback");
}
{
    var runner = new FakeRouteRunner { IgnoreDelete = "128.0.0.0/1" };
    var manager = new RouteLeaseManager(runner);
    await manager.StartAsync(42, false, CancellationToken.None);
    if (await manager.StopAsync(CancellationToken.None))
        throw new Exception("Cleanup reported success with a route still installed.");
    runner.IgnoreDelete = null;
    if (!await manager.StopAsync(CancellationToken.None))
        throw new Exception("Incomplete route cleanup could not be retried.");
    Console.WriteLine("PASS: route deletion is verified before cleanup succeeds");
}
{
    if(!NetshRouteRunner.IsAllowed(TunnelRouteCommands.AddIpv4(42,"0.0.0.0/1"),42,77))throw new Exception("Valid route command rejected");
    var malicious=TunnelRouteCommands.AddIpv4(42,"0.0.0.0/1");malicious[^1]="store=persistent";
    if(NetshRouteRunner.IsAllowed(malicious,42,77) || NetshRouteRunner.IsAllowed(new[]{"interface","ipv4","delete","route","0.0.0.0/0","42"},42,77) ||
        NetshRouteRunner.IsAllowed(TunnelRouteCommands.AddIpv4(43,"0.0.0.0/1"),42,77))
        throw new Exception("Runner accepted an unauthorized route command");
    Console.WriteLine("PASS: route runner rejects arbitrary and persistent commands");
}
{
    using var job=new ChildProcessJob();
    var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")) { UseShellExecute=false,CreateNoWindow=true };
    start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-Command");start.ArgumentList.Add("Start-Sleep -Seconds 30");
    using var child=System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start isolated test child");
    job.Assign(child);job.Dispose();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: closing a child process job terminates its process");
}
{
    var runner=new FakeRouteRunner { Preexisting=true };
    var manager=new RouteLeaseManager(runner);
    var rejected=false;try{await manager.StartAsync(42,false,CancellationToken.None);}catch(IOException){rejected=true;}
    if(!rejected || runner.Calls.Any(call=>call.Contains("delete"))) throw new Exception("Preexisting route was accepted or removed");
    Console.WriteLine("PASS: route activation leaves preexisting routes untouched");
}
{
    var runner=new FakeRouteRunner();runner.FailCalls.UnionWith(new[]{2,3});
    var manager=new RouteLeaseManager(runner);var pending=false;
    try{await manager.StartAsync(42,false,CancellationToken.None);}catch(NetworkRecoveryPendingException){pending=true;}
    if(!pending)throw new Exception("Failed cleanup was not reported to the supervisor");
    runner.FailCalls.Clear();if(!await manager.StopAsync(CancellationToken.None))throw new Exception("Pending recovery could not be retried");
    Console.WriteLine("PASS: partial route cleanup failures remain explicit and retryable");
}
{
    var routes=WindowsRouteTable.Read();
    if(!routes.Any(route=>route.Prefix=="0.0.0.0/0" && route.InterfaceIndex>0))throw new Exception("Read-only route inventory missed the active IPv4 default");
    Console.WriteLine("PASS: native read-only route inventory finds an IPv4 default route");
}
{
    var adapter=Guid.NewGuid();var routes=new List<WindowsRoute>();
    var routeSnapshot=new PrivateAdapterSnapshot(adapter,123,42,42,routes);
    var inspector=new PrivateRouteInspector(adapter,123,()=>routeSnapshot);
    if(await inspector.InspectAsync(42,"0.0.0.0/1",false,CancellationToken.None)!=PrivateRouteState.Absent)throw new Exception("Empty private route was not absent");
    await inspector.PrepareAsync(42,"0.0.0.0/1",false,CancellationToken.None);
    routes.Add(new WindowsRoute(123,42,"0.0.0.0/1",IPAddress.Parse("198.18.0.1"),1,12,false));
    if(await inspector.InspectAsync(42,"0.0.0.0/1",false,CancellationToken.None)!=PrivateRouteState.Owned)throw new Exception("Prepared matching route was not owned");
    routes[0] = routes[0] with { ValidLifetime = 120 };
    if(await inspector.InspectAsync(42,"0.0.0.0/1",false,CancellationToken.None)!=PrivateRouteState.Conflict)throw new Exception("Long-lived route was treated as an owned lease");
    routeSnapshot=routeSnapshot with { AdapterId=Guid.NewGuid() };
    if(await inspector.InspectAsync(42,"0.0.0.0/1",false,CancellationToken.None)!=PrivateRouteState.Conflict)throw new Exception("Reused index was trusted");
    Console.WriteLine("PASS: private route inspection rejects adapter identity replacement");
}
{
    var adapter = Guid.NewGuid();
    var currentRoutes = new List<WindowsRoute>();
    var inspector = new PrivateRouteInspector(adapter, 123, () => null, () => currentRoutes);
    var token = CancellationToken.None;
    if (await inspector.InspectAsync(42, "0.0.0.0/1", false, token) != PrivateRouteState.AdapterGone)
        throw new Exception("A disappeared adapter with no remaining routes should be safe to clean up.");
    currentRoutes.Add(new WindowsRoute(456, 42, "0.0.0.0/1", IPAddress.Any, 1, 5, false));
    if (await inspector.InspectAsync(42, "0.0.0.0/1", false, token) != PrivateRouteState.AdapterGone)
        throw new Exception("An unrelated adapter reusing the index should be preserved.");
    currentRoutes.Add(new WindowsRoute(123, 42, "0.0.0.0/1", IPAddress.Any, 1, 5, false));
    if (await inspector.InspectAsync(42, "0.0.0.0/1", false, token) != PrivateRouteState.Conflict)
        throw new Exception("An orphaned route must keep recovery pending.");
    Console.WriteLine("PASS: engine-loss recovery distinguishes expired routes from orphaned routes");
}

{
    var route=WindowsRouteTable.Read().First(x=>x.Prefix=="0.0.0.0/0");
    var adapter=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(x=>x.GetIPProperties().GetIPv4Properties()?.Index==route.InterfaceIndex);
    var adapterSnapshot=WindowsPrivateAdapterSnapshot.Read(Guid.Parse(adapter.Id));
    if(adapterSnapshot is null || adapterSnapshot.Luid!=route.InterfaceLuid || adapterSnapshot.Ipv4Index!=route.InterfaceIndex)throw new Exception("Adapter GUID, LUID and index do not agree");
    Console.WriteLine("PASS: read-only adapter identity agrees with native route LUID");
}
{
    var name="TetherBridge-"+Guid.NewGuid().ToString("N");
    var engineArgs=TunnelEngineLauncher.BuildArguments(name);
    if(!engineArgs.Contains("tun://"+name) || !engineArgs.Contains("socks5://127.0.0.1:10810"))throw new Exception("Unexpected tunnel engine arguments");
    var bad=false;try{TunnelEngineLauncher.BuildArguments("other;--foo");}catch(ArgumentException){bad=true;}
    if(!bad)throw new Exception("Untrusted tunnel adapter name was accepted");
    Console.WriteLine("PASS: tunnel engine arguments use a private adapter and fixed loopback relay");
}
{
    using var job=new ChildProcessJob();
    var exe=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    using var child=job.StartSuspendedThenResume(exe,new[]{"-NoProfile","-Command","Start-Sleep -Seconds 30"},Environment.SystemDirectory);
    if(child.HasExited)throw new Exception("Job child exited before test");
    job.Dispose();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: child is assigned to job before execution and terminates on close");
}
{
    var admins=new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid,null);
    var world=new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid,null);
    var trusted=new System.Security.AccessControl.FileSystemAccessRule(admins,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.AccessControlType.Allow);
    var unsafeRule=new System.Security.AccessControl.FileSystemAccessRule(world,System.Security.AccessControl.FileSystemRights.WriteData,System.Security.AccessControl.AccessControlType.Allow);
    var inheritedWriter=new System.Security.AccessControl.FileSystemAccessRule(world,System.Security.AccessControl.FileSystemRights.WriteData,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.InheritOnly,System.Security.AccessControl.AccessControlType.Allow);
    if(!TunnelPathTrust.IsTrustedAcl(admins,new[]{trusted}) || TunnelPathTrust.IsTrustedAcl(admins,new[]{trusted,unsafeRule}) ||
        !TunnelPathTrust.IsDangerousAccessMask(0x40000000) || TunnelPathTrust.IsTrustedAcl(admins,new[]{trusted,inheritedWriter},true) || TunnelPathTrust.IsTrustedAcl(world,new[]{trusted}))
        throw new Exception("Tunnel dependency ACL validation did not reject untrusted write/ownership");
    Console.WriteLine("PASS: elevated tunnel dependency ACL rejects untrusted writers and owners");
}
{
    var query = new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var dns=new LocalDnsProxy();dns.Start((_,_)=>Task.FromException<byte[]>(new IOException("unreachable")));
    try {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await udp.SendAsync(query,new IPEndPoint(IPAddress.Loopback,dns.Port),timeout.Token);var reply=await udp.ReceiveAsync(timeout.Token);
        if(!DnsMessageValidation.MatchesResponse(query,reply.Buffer)||(reply.Buffer[3]&15)!=2)throw new Exception("UDP resolver failure did not produce SERVFAIL");
        using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,dns.Port,timeout.Token);var stream=tcp.GetStream();
        await stream.WriteAsync(new byte[]{0,(byte)query.Length},timeout.Token);await stream.WriteAsync(query,timeout.Token);var length=new byte[2];await stream.ReadExactlyAsync(length,timeout.Token);
        var body=new byte[(length[0]<<8)|length[1]];await stream.ReadExactlyAsync(body,timeout.Token);
        if(!DnsMessageValidation.MatchesResponse(query,body)||(body[3]&15)!=2)throw new Exception("TCP resolver failure did not produce SERVFAIL");
        Console.WriteLine("PASS: DNS proxy returns SERVFAIL promptly when upstream fails");
    } finally {dns.Stop();}
}
{
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var broken=(byte[])query.Clone();broken[2]=0x81;broken[3]=0x80;broken[7]=1;
    if(DnsMessageValidation.MatchesResponse(query,broken))throw new Exception("Truncated DNS answer accepted");
    var valid=broken.Concat(new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,1,2,3,4}).ToArray();
    if(!DnsMessageValidation.MatchesResponse(query,valid))throw new Exception("Valid compressed DNS answer rejected");
    var malformed=(byte[])valid.Clone();malformed[query.Length]=0xc0;malformed[query.Length+1]=(byte)query.Length;
    if(DnsMessageValidation.MatchesResponse(query,malformed))throw new Exception("DNS compression loop accepted");
    Console.WriteLine("PASS: DNS response validation rejects missing records and compression loops");
}
{
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var pending=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var dns=new LocalDnsProxy();dns.Start((_,_)=>pending.Task);
    try {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await udp.SendAsync(query,new IPEndPoint(IPAddress.Loopback,dns.Port),timeout.Token);var answer=await udp.ReceiveAsync(timeout.Token);
        if(!DnsMessageValidation.MatchesResponse(query,answer.Buffer)||(answer.Buffer[3]&15)!=2)throw new Exception("Non-cooperative DNS resolver did not time out with SERVFAIL");
        Console.WriteLine("PASS: non-cooperative DNS resolver cannot hold a client indefinitely");
    } finally {pending.TrySetResult(Array.Empty<byte>());dns.Stop();}
}
{
    using var job=new ChildProcessJob();var exe=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    using var child=job.StartSuspendedThenResume(exe,new[]{"-NoProfile","-Command","if ($env:PATH -eq (Join-Path $env:SystemRoot 'System32')) { exit 0 } else { exit 1 }"},Environment.SystemDirectory,restrictedEnvironment:true);
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if(child.ExitCode!=0)throw new Exception("Restricted child inherited an unsafe PATH");
    Console.WriteLine("PASS: tunnel child inherits a restricted system-only PATH");
}
{
    var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    start.ArgumentList.Add("job-crash-host");
    using var host=System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start job test host");
    var line=await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if(!int.TryParse(line,out var childId)) { host.Kill(); throw new Exception("Job test host did not report a child"); }
    using var child=System.Diagnostics.Process.GetProcessById(childId);
    host.Kill();await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: unexpected tunnel host termination kills its job-owned child");
}
{
    var job=new ChildProcessJob();var exe=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    var child=job.StartSuspendedThenResume(exe,new[]{"-NoProfile","-Command","Start-Sleep -Seconds 30"},Environment.SystemDirectory);
    using var engine=new TunnelEngine(child,job,Guid.NewGuid());
    await engine.StopAsync(CancellationToken.None);
    if(!child.HasExited)throw new Exception("Tunnel engine stop returned before child exit");
    Console.WriteLine("PASS: tunnel engine stop waits for child exit and adapter absence");
}
{
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var pending=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);int active=0;
    var dns=new LocalDnsProxy();dns.Start((_,_)=>{Interlocked.Increment(ref active);return pending.Task;});
    try {
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var endpoint=new IPEndPoint(IPAddress.Loopback,dns.Port);
        for(int i=0;i<64;i++)await udp.SendAsync(query,endpoint);
        using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(4));while(Volatile.Read(ref active)<64)await Task.Delay(10,ready.Token);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));await udp.SendAsync(query,endpoint,deadline.Token);
        var answer=await udp.ReceiveAsync(deadline.Token);
        if(!DnsMessageValidation.MatchesResponse(query,answer.Buffer)||(answer.Buffer[3]&15)!=2)throw new Exception("Saturated DNS proxy did not return SERVFAIL");
        Console.WriteLine("PASS: saturated UDP DNS proxy returns SERVFAIL instead of silently dropping");
    } finally {pending.TrySetResult(Array.Empty<byte>());dns.Stop();}
}
{
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var dns=new LocalDnsProxy();dns.Start((_,_)=>{Thread.Sleep(6000);return Task.FromResult(Array.Empty<byte>());});
    try {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await udp.SendAsync(query,new IPEndPoint(IPAddress.Loopback,dns.Port),timeout.Token);var answer=await udp.ReceiveAsync(timeout.Token);
        if(!DnsMessageValidation.MatchesResponse(query,answer.Buffer)||(answer.Buffer[3]&15)!=2)throw new Exception("Synchronously blocked resolver did not produce SERVFAIL");
        Console.WriteLine("PASS: synchronously blocked DNS resolver cannot hold a client indefinitely");
    } finally {dns.Stop();}
}
{
    var commands=TunnelAdapterCommands.Build(42,77);
    if(commands.Length!=4 || !commands[0].Contains("name=42") || !commands[0].Contains("store=active") ||
       !commands[1].Contains("address=127.0.0.1") || !commands[2].Contains("interface=77") || !commands[2].Contains("store=active") ||
       !commands[3].Contains("name=77") || !commands[3].Contains("address=::1"))
        throw new Exception("Tunnel adapter setup is not restricted to the intended interfaces and loopback DNS");
    var rejected=false;try{TunnelAdapterCommands.Build(0,77);}catch(ArgumentException){rejected=true;}
    if(!rejected)throw new Exception("Tunnel adapter configuration accepted invalid index");
    Console.WriteLine("PASS: tunnel adapter commands target explicit interfaces and private DNS");
}
{
    var id=Guid.NewGuid();var state=new TunnelAdapterState(id,123,42,77,Array.Empty<IPAddress>(),Array.Empty<IPAddress>());
    var calls=new List<string[]>();var others=new List<IPAddress>{IPAddress.Parse("198.18.0.88")};
    var configurator=new TunnelAdapterConfigurator(id,123,()=>state,()=>others,()=>Array.Empty<WindowsRoute>(),()=>true,(args,_)=>{calls.Add(args);return Task.CompletedTask;});
    var rejected=false;try{await configurator.ConfigureAsync(CancellationToken.None);}catch(IOException){rejected=true;}
    if(!rejected || calls.Count!=0)throw new Exception("Conflicting tunnel subnet was modified");
    Console.WriteLine("PASS: private adapter setup refuses an overlapping host subnet before mutation");
}
{
    var id=Guid.NewGuid();var state=new TunnelAdapterState(id,123,42,77,Array.Empty<IPAddress>(),Array.Empty<IPAddress>());
    var calls=new List<string[]>();
    var setup=new TunnelAdapterConfigurator(id,123,()=>state,()=>Array.Empty<IPAddress>(),()=>Array.Empty<WindowsRoute>(),()=>true,(args,_)=>{
        calls.Add(args);
        if(args[1]=="ipv4" && args[3]=="address")state=state with {Addresses=state.Addresses.Append(IPAddress.Parse("198.18.0.1")).ToArray()};
        if(args[1]=="ipv4" && args[3]=="dnsservers")state=state with {DnsServers=new[]{IPAddress.Loopback}};
        if(args[1]=="ipv6" && args[3]=="address")state=state with {Addresses=state.Addresses.Append(IPAddress.Parse("fd7a:6272:6964::1")).ToArray()};
        if(args[1]=="ipv6" && args[3]=="dnsservers")state=state with {DnsServers=state.DnsServers.Append(IPAddress.IPv6Loopback).ToArray()};
        return Task.CompletedTask;
    });
    await setup.ConfigureAsync(CancellationToken.None);if(calls.Count!=4)throw new Exception("Private adapter commands were not completed");
    state=new TunnelAdapterState(id,123,42,77,Array.Empty<IPAddress>(),Array.Empty<IPAddress>());calls.Clear();
    var changed=new TunnelAdapterConfigurator(id,123,()=>state,()=>Array.Empty<IPAddress>(),()=>Array.Empty<WindowsRoute>(),()=>true,(args,_)=>{calls.Add(args);state=state with {AdapterId=Guid.NewGuid()};return Task.CompletedTask;});
    var rejected=false;try{await changed.ConfigureAsync(CancellationToken.None);}catch(IOException){rejected=true;}
    if(!rejected || calls.Count!=1)throw new Exception("Changed adapter identity was not rejected before next command");
    Console.WriteLine("PASS: adapter setup verifies results and stops after identity change");
}
{
    var valid=TunnelAdapterCommands.Build(42,77)[0];
    if(!WindowsTunnelAdapterBackend.IsAllowed(valid,42,77))throw new Exception("Valid private adapter command rejected");
    var malicious=(string[])valid.Clone();malicious[4]="name=43";
    if(WindowsTunnelAdapterBackend.IsAllowed(malicious,42,77))throw new Exception("Command for a different adapter accepted");
    Console.WriteLine("PASS: Windows adapter backend only permits its fixed private-adapter commands");
}
{
    var dns=new LocalDnsProxy();dns.Start((q,_)=>{var a=(byte[])q.Clone();a[2]|=0x80;return Task.FromResult(a);});
    try {
        using var rival=new UdpClient(AddressFamily.InterNetwork);rival.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
        var rejected=false;try{rival.Client.Bind(new IPEndPoint(IPAddress.Loopback,dns.Port));}catch(SocketException){rejected=true;}
        if(!rejected)throw new Exception("Another process can bind the owned DNS UDP endpoint");
        Console.WriteLine("PASS: local DNS UDP listener rejects competing binds");
    }finally{dns.Stop();}
}
{
    var id=Guid.NewGuid();var state=new TunnelAdapterState(id,123,42,77,Array.Empty<IPAddress>(),Array.Empty<IPAddress>());
    var routes=new[]{new WindowsRoute(456,12,"198.18.0.0/16",IPAddress.Any,1,100,false)};int calls=0;
    var setup=new TunnelAdapterConfigurator(id,123,()=>state,()=>new[]{IPAddress.Parse("198.18.1.1")},()=>routes,()=>true,(args,_)=>{calls++;return Task.CompletedTask;});
    var rejected=false;try{await setup.ConfigureAsync(CancellationToken.None);}catch(IOException){rejected=true;}
    if(!rejected||calls!=0 || !TunnelAdapterConfigurator.OverlapsReservedRoute("fd7a:6272::/32") ||
       TunnelAdapterConfigurator.OverlapsReservedRoute("0.0.0.0/0") || TunnelAdapterConfigurator.OverlapsReservedRoute("10.0.0.0/8") ||
       !TunnelAdapterConfigurator.OverlapsReservedRoute("malformed")) throw new Exception("Route overlap validation failed");
    Console.WriteLine("PASS: private adapter setup rejects overlapping route prefixes");
}
if (Socket.OSSupportsIPv6) {
    var query=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var dns=new LocalDnsProxy();dns.Start((q,_)=>{var a=(byte[])q.Clone();a[2]|=0x80;return Task.FromResult(a);});
    try {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));using var udp=new UdpClient(AddressFamily.InterNetworkV6);
        await udp.SendAsync(query,new IPEndPoint(IPAddress.IPv6Loopback,dns.Port),timeout.Token);var answer=await udp.ReceiveAsync(timeout.Token);
        if(!DnsMessageValidation.MatchesResponse(query,answer.Buffer))throw new Exception("IPv6 loopback DNS UDP failed");
        using var tcp=new TcpClient(AddressFamily.InterNetworkV6);await tcp.ConnectAsync(IPAddress.IPv6Loopback,dns.Port,timeout.Token);var stream=tcp.GetStream();
        await stream.WriteAsync(new byte[]{0,(byte)query.Length},timeout.Token);await stream.WriteAsync(query,timeout.Token);var length=new byte[2];await stream.ReadExactlyAsync(length,timeout.Token);
        var body=new byte[(length[0]<<8)|length[1]];await stream.ReadExactlyAsync(body,timeout.Token);if(!DnsMessageValidation.MatchesResponse(query,body))throw new Exception("IPv6 loopback DNS TCP failed");
        Console.WriteLine("PASS: local DNS supports IPv6 loopback UDP and TCP");
    }finally{dns.Stop();}
}
{
    if(!TunnelRouteCoverage.CanBypass("8.8.8.0/24") || !TunnelRouteCoverage.CanBypass("2001:db8::/32") || !TunnelRouteCoverage.CanBypass("0.0.0.0/1") ||
       TunnelRouteCoverage.CanBypass("10.1.19.0/24") || TunnelRouteCoverage.CanBypass("127.0.0.0/8") || TunnelRouteCoverage.CanBypass("::1/128") ||
       TunnelRouteCoverage.CanBypass("0.0.0.0/0") || !TunnelRouteCoverage.CanBypass("invalid"))
        throw new Exception("Full-tunnel route coverage classification failed");
    Console.WriteLine("PASS: full-tunnel preflight flags more-specific public routes");
}
{
    var runtime=new FakeTunnelRuntime{FailDuringRoutes=true};var supervisor=new TunnelSessionSupervisor(runtime);
    var failed=false;try{await supervisor.StartAsync(CancellationToken.None);}catch(IOException){failed=true;}
    if(!failed || string.Join(",",runtime.Events)!="preflight,engine,configure,routes,remove-routes,stop-engine")
        throw new Exception("Failed tunnel setup did not unwind in order");
    Console.WriteLine("PASS: tunnel supervisor unwinds partial route setup before stopping engine");
}
// An uncertain route removal must retain the engine and allow explicit retry.
{
    var runtime = new FakeTunnelRuntime { FailDuringRoutes = true, RefuseRemoval = true };
    var supervisor = new TunnelSessionSupervisor(runtime);
    try { await supervisor.StartAsync(CancellationToken.None); throw new Exception("Expected failure"); }
    catch (NetworkRecoveryPendingException) { }
    if (runtime.Events.Contains("stop-engine")) throw new Exception("Engine stopped before route recovery");
    runtime.RefuseRemoval = false;
    await supervisor.StopAsync(CancellationToken.None);
    if (string.Join(",", runtime.Events) != "preflight,engine,configure,routes,remove-routes,remove-routes,stop-engine") throw new Exception("Recovery retry order incorrect");
    Console.WriteLine("PASS: tunnel supervisor retains engine until route recovery succeeds");
}
// Loss of the underlying tunnel must stop routes before the engine.
{
    var runtime = new FakeTunnelRuntime();
    var supervisor = new TunnelSessionSupervisor(runtime);
    await supervisor.StartAsync(CancellationToken.None);
    var monitor = new TunnelLeaseMonitor(supervisor, _ => Task.FromResult(false), TimeSpan.FromMilliseconds(5));
    try { await monitor.RunAsync(CancellationToken.None); throw new Exception("Expected health failure"); }
    catch (IOException) { }
    if (string.Join(",", runtime.Events) != "preflight,engine,configure,routes,remove-routes,stop-engine") throw new Exception("Failed health must tear down routes first");
    Console.WriteLine("PASS: tunnel health failure stops routes before engine");
}
// Healthy sessions renew until cancellation, then release routes before the engine.
{
    var runtime = new FakeTunnelRuntime(); var supervisor = new TunnelSessionSupervisor(runtime);
    await supervisor.StartAsync(CancellationToken.None);
    using var cancel = new CancellationTokenSource();
    var monitor = new TunnelLeaseMonitor(supervisor, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(5));
    var running = monitor.RunAsync(cancel.Token);
    await runtime.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await running;
    var events = runtime.Events.ToArray();
    if (!events.Contains("renew") || events[^2] != "remove-routes" || events[^1] != "stop-engine") throw new Exception("Lease monitor did not renew and shut down in order");
    Console.WriteLine("PASS: tunnel monitor renews healthy lease and cleans up on cancellation");
}
// A synchronously blocked read-only health probe cannot prevent cancellation and cleanup.
{
    var runtime = new FakeTunnelRuntime(); var supervisor = new TunnelSessionSupervisor(runtime);
    await supervisor.StartAsync(CancellationToken.None);
    using var cancel = new CancellationTokenSource(); using var releaseProbe = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<bool> Health(CancellationToken _) { entered.TrySetResult(); releaseProbe.Wait(); return Task.FromResult(true); }
    var monitor = new TunnelLeaseMonitor(supervisor, Health, TimeSpan.FromMilliseconds(5));
    var running = monitor.RunAsync(cancel.Token);
    try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(1)); }
    finally { releaseProbe.Set(); }
    if (!runtime.Events.Contains("stop-engine")) throw new Exception("Blocked health probe prevented tunnel cleanup");
    Console.WriteLine("PASS: blocked tunnel health probe cannot prevent cancellation cleanup");
}
// A truncated UDP answer must be recoverable by retrying against the TCP resolver.
{
    var query = new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};
    var truncated = (byte[])query.Clone(); truncated[2] = 0x83; truncated[3] = 0x80;
    var complete = (byte[])query.Clone(); complete[2] = 0x81; complete[3] = 0x80;
    var udpCalls = 0; var tcpCalls = 0;
    var dns = new LocalDnsProxy();
    dns.Start((q, ct) => { Interlocked.Increment(ref udpCalls); return Task.FromResult(truncated); },
        tcpResolver: (q, ct) => { Interlocked.Increment(ref tcpCalls); return Task.FromResult(complete); });
    try {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, dns.Port), timeout.Token);
        var first = await udp.ReceiveAsync(timeout.Token);
        if ((first.Buffer[2] & 2) == 0) throw new Exception("DNS UDP truncation was lost");
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, dns.Port, timeout.Token);
        var stream = tcp.GetStream(); await stream.WriteAsync(new byte[] {0, (byte)query.Length}, timeout.Token); await stream.WriteAsync(query, timeout.Token);
        var length = new byte[2]; await stream.ReadExactlyAsync(length, timeout.Token);
        var response = new byte[(length[0] << 8) | length[1]]; await stream.ReadExactlyAsync(response, timeout.Token);
        if ((response[2] & 2) != 0 || udpCalls != 1 || tcpCalls != 1) throw new Exception("DNS TCP retry did not use the TCP resolver");
        Console.WriteLine("PASS: DNS TCP retry uses separate resolver after truncated UDP answer");
    } finally { dns.Stop(); }
}
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
var relay = new LocalSocks5Relay();
relay.Start(new PhoneBridgeSession(new UnusedTransport(), null), port, Console.WriteLine);
try {
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
    var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 2 });
    var response = new byte[2]; await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    if (response[0] != 5 || response[1] != 255) throw new Exception($"Expected 05 FF when no supported method is offered; got {Convert.ToHexString(response)}");
    Console.WriteLine("PASS: unsupported SOCKS authentication methods rejected");
} finally { relay.Stop(); }

var tempDir = Path.Combine(Path.GetTempPath(), "TetherBridgeTest-" + Guid.NewGuid());
Directory.CreateDirectory(tempDir);
var snapshot = Path.Combine(tempDir, "20260101-000000.json");
File.WriteAllText(snapshot, "{\"ProxyEnable\":0,\"ProxyServer\":null,\"ProxyOverride\":null,\"AutoConfigUrl\":null,\"AutoDetect\":null}");
try {
    var restored = false;
    var store = new ProxyProfileStore(tempDir, () => false, _ => restored = true);
    var didRestore = store.TryRestoreLatest(out _);
    if (didRestore || restored) throw new Exception("A stale snapshot overwrote a proxy no longer owned by TetherBridge");
    Console.WriteLine("PASS: other software's current proxy is preserved");
    store = new ProxyProfileStore(tempDir, () => true, _ => restored = true);
    if (store.TryRestoreLatest(out _) || restored) throw new Exception("Historical snapshot used without an active marker");
    File.WriteAllText(Path.Combine(tempDir, "active.snapshot"), Path.GetFileName(snapshot));
    if (!store.TryRestoreLatest(out _) || !restored) throw new Exception("Active TetherBridge proxy was not restored");
    Console.WriteLine("PASS: TetherBridge-owned proxy is restored");
    File.WriteAllText(snapshot, "{invalid json");
    File.WriteAllText(Path.Combine(tempDir, "active.snapshot"), Path.GetFileName(snapshot));
    restored = false;
    if (store.TryRestoreLatest(out _) || restored) throw new Exception("Corrupt snapshot was restored");
    Console.WriteLine("PASS: corrupt snapshot is reported without changing proxy");
} finally { File.Delete(snapshot); File.Delete(Path.Combine(tempDir, "active.snapshot")); Directory.Delete(tempDir); }

var actions = new List<string>();
await ConnectionTransition.PrepareAsync(() => true, () => { actions.Add("restore"); actions.Add("stop"); return Task.CompletedTask; }, () => { actions.Add("stop"); return Task.CompletedTask; });
if (string.Join(",", actions) != "restore,stop") throw new Exception("Switch must restore the owned proxy before stopping the relay");
actions.Clear();
await ConnectionTransition.PrepareAsync(() => false, () => { actions.Add("restore"); return Task.CompletedTask; }, () => { actions.Add("stop"); return Task.CompletedTask; });
if (string.Join(",", actions) != "stop") throw new Exception("Switch must preserve another program's proxy");
actions.Clear();
try {
    await ConnectionTransition.PrepareAsync(() => true, () => throw new InvalidOperationException("restore failed"), () => { actions.Add("stop"); return Task.CompletedTask; });
    throw new Exception("Expected restoration failure");
} catch (InvalidOperationException) { }
if (actions.Count != 0) throw new Exception("Relay stopped despite proxy restoration failure");
Console.WriteLine("PASS: switching modes safely prepares the current connection");

string? command = null;
var allocation = await AdbForwardAllocator.CreateAsync(10888, args => { command = args; return Task.FromResult((0, "23456\n", "")); });
if (command != "forward tcp:0 tcp:10888" || !allocation.Result.Ok || allocation.Port != 23456) throw new Exception("USB forwarding must request and use an automatically allocated local port");
var invalidAllocation = await AdbForwardAllocator.CreateAsync(10888, _ => Task.FromResult((0, "unexpected", "")));
if (invalidAllocation.Result.Ok || invalidAllocation.Port != 0) throw new Exception("An invalid ADB port must not be accepted");
Console.WriteLine("PASS: USB forwarding uses an allocated port and validates the result");
var forwards = "phoneA tcp:23456 tcp:10888\nphoneB tcp:8739 tcp:8739\n";
if (!AdbForwardAllocator.IsOwnedMapping(forwards, "phoneA", 23456, 10888)) throw new Exception("Owned forward not found");
if (AdbForwardAllocator.IsOwnedMapping(forwards, "phoneB", 23456, 10888) || AdbForwardAllocator.IsOwnedMapping(forwards, "phoneA", 8739, 8739)) throw new Exception("Another device's forward must not be removed");
Console.WriteLine("PASS: only the matching device and mapping can be released");

var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
var tempListener = new TcpListener(IPAddress.Loopback, 0); tempListener.Start();
var relayPort = ((IPEndPoint)tempListener.LocalEndpoint).Port; tempListener.Stop();
var testRelay = new LocalRelayProxy();
testRelay.Start(new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1", originPort), null), relayPort, Console.WriteLine);
try {
    var originTask = Task.Run(async () => {
        using var server = await origin.AcceptTcpClientAsync();
        var s = server.GetStream(); var buffer = new byte[4096];
        while (await s.ReadAsync(buffer) != 0) { }
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"));
        server.Client.Shutdown(SocketShutdown.Send);
    });
    using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, relayPort);
    var stream = c.GetStream();
    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"));
    c.Client.Shutdown(SocketShutdown.Send);
    using var response = new MemoryStream();
    await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(5));
    await originTask.WaitAsync(TimeSpan.FromSeconds(5));
    if (!System.Text.Encoding.ASCII.GetString(response.ToArray()).EndsWith("OK")) throw new Exception("HTTP relay lost response after client half-close");
    Console.WriteLine("PASS: HTTP relay returns response after client half-close");
} finally { testRelay.Stop(); origin.Stop(); }

origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
tempListener = new TcpListener(IPAddress.Loopback, 0); tempListener.Start();
relayPort = ((IPEndPoint)tempListener.LocalEndpoint).Port; tempListener.Stop();
var socksTest = new LocalSocks5Relay();
socksTest.Start(new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1", originPort), null), relayPort, Console.WriteLine);
try {
    var originTask = Task.Run(async () => {
        using var server = await origin.AcceptTcpClientAsync(); var s = server.GetStream();
        var end = new byte[] {13,10,13,10}; var matched = 0; var one = new byte[1];
        while (matched < 4) { await s.ReadExactlyAsync(one); matched = one[0] == end[matched] ? matched + 1 : 0; }
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
        var buffer = new byte[1024]; while (await s.ReadAsync(buffer) != 0) { }
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("reply")); server.Client.Shutdown(SocketShutdown.Send);
    });
    using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, relayPort); var stream = c.GetStream();
    await stream.WriteAsync(new byte[] {5,1,0}); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting);
    await stream.WriteAsync(new byte[] {5,1,0,1,127,0,0,1,0,80}); var reply = new byte[10]; await stream.ReadExactlyAsync(reply);
    if (reply[1] != 0) throw new Exception("SOCKS CONNECT failed");
    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("request")); c.Client.Shutdown(SocketShutdown.Send);
    using var response = new MemoryStream(); await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(5));
    await originTask.WaitAsync(TimeSpan.FromSeconds(5));
    if (System.Text.Encoding.ASCII.GetString(response.ToArray()) != "reply") throw new Exception("SOCKS relay lost response after client half-close");
    Console.WriteLine("PASS: SOCKS relay returns response after client half-close");
} finally { socksTest.Stop(); origin.Stop(); }

var unobserved = 0;
EventHandler<UnobservedTaskExceptionEventArgs> observer = (_, e) => { Interlocked.Increment(ref unobserved); e.SetObserved(); };
TaskScheduler.UnobservedTaskException += observer;
try {
    var stressRelay = new LocalRelayProxy();
    var stressSocks = new LocalSocks5Relay();
    var stressSession = new PhoneBridgeSession(new UnusedTransport(), null);
    for (var i = 0; i < 500; i++) { stressRelay.Start(stressSession, 0, _ => {}); stressRelay.Stop(); stressSocks.Start(stressSession, 0, _ => {}); stressSocks.Stop(); }
    await Task.Delay(500);
    for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100); }
    if (unobserved != 0) throw new Exception($"Relay start/stop produced {unobserved} unobserved task exceptions");
    Console.WriteLine("PASS: repeated relay start/stop leaves no faulted tasks");
} finally { TaskScheduler.UnobservedTaskException -= observer; }

var validPin = new string('A', 64);
if (!PairingPayloadParser.TryParse("tetherbridge://pair?mode=wifi&host=192.0.2.1&port=10888&token=secret&fingerprint=" + validPin, out var paired, out _) || paired.Fingerprint != validPin) throw new Exception("Secure pairing fingerprint was not retained");
if (PairingPayloadParser.TryParse("tetherbridge://pair?mode=wifi&host=192.0.2.1&token=secret", out _, out _)) throw new Exception("Insecure pairing without a fingerprint must be rejected");
if (PairingPayloadParser.TryParse("tetherbridge://pair?mode=wifi&host=192.0.2.1&token=secret&fingerprint=abcd", out _, out _)) throw new Exception("Malformed fingerprint must be rejected");
Console.WriteLine("PASS: wireless pairing requires a valid certificate fingerprint");

using var key = System.Security.Cryptography.RSA.Create(2048);
var certRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
using var generatedCert = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(generatedCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), "", System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
var pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData));
async Task<bool> TryTls(string fingerprint) {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () => {
        try {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var ssl = new System.Net.Security.SslStream(peer.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            var b = new byte[1]; await ssl.ReadExactlyAsync(b, timeout.Token);
            await ssl.WriteAsync(b, timeout.Token);
        } catch (Exception) { }
    });
    var succeeded = false;
    try {
        var transport = new TcpPhoneTransport("127.0.0.1", port, fingerprint);
        await using var stream = await transport.ConnectAsync(timeout.Token);
        await stream.WriteAsync(new byte[] {42}, timeout.Token);
        var b = new byte[1]; await stream.ReadExactlyAsync(b, timeout.Token);
        succeeded = b[0] == 42;
    } catch (Exception) { }
    finally { listener.Stop(); await server.WaitAsync(TimeSpan.FromSeconds(6)); }
    return succeeded;
}
if (!await TryTls(pin)) throw new Exception("Pinned TLS connection to the expected certificate failed");
if (await TryTls(new string('0', 64))) throw new Exception("TLS accepted an unexpected certificate");
Console.WriteLine("PASS: wireless TLS accepts only the pinned certificate");
var tlsOrigin = new TcpListener(IPAddress.Loopback, 0); tlsOrigin.Start();
var tlsOriginPort = ((IPEndPoint)tlsOrigin.LocalEndpoint).Port;
var tempTlsListener = new TcpListener(IPAddress.Loopback, 0); tempTlsListener.Start();
var tlsRelayPort = ((IPEndPoint)tempTlsListener.LocalEndpoint).Port; tempTlsListener.Stop();
var tlsRelay = new LocalRelayProxy();
tlsRelay.Start(new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1", tlsOriginPort, pin), null), tlsRelayPort, Console.WriteLine);
try {
    var originTask = Task.Run(async () => {
        using var peer = await tlsOrigin.AcceptTcpClientAsync();
        using var ssl = new System.Net.Security.SslStream(peer.GetStream());
        await ssl.AuthenticateAsServerAsync(cert);
        var buf = new byte[2048]; while (await ssl.ReadAsync(buf) != 0) { }
        await ssl.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"));
        await ssl.ShutdownAsync();
    });
    using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, tlsRelayPort); var stream = c.GetStream();
    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"));
    c.Client.Shutdown(SocketShutdown.Send);
    using var response = new MemoryStream(); await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(6));
    await originTask.WaitAsync(TimeSpan.FromSeconds(6));
    if (!System.Text.Encoding.ASCII.GetString(response.ToArray()).EndsWith("OK")) throw new Exception("TLS relay lost response after half-close");
    Console.WriteLine("PASS: TLS relay preserves response after half-close");
} finally { tlsRelay.Stop(); tlsOrigin.Stop(); }

var frameListener = new TcpListener(IPAddress.Loopback, 0); frameListener.Start();
var framePort = ((IPEndPoint)frameListener.LocalEndpoint).Port;
try {
    var server = Task.Run(async () => {
        using var peer = await frameListener.AcceptTcpClientAsync(); var stream = peer.GetStream();
        var header = new byte[4]; await stream.ReadExactlyAsync(header);
        int n = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
        if (n != 7) throw new Exception("Unexpected frame length " + n);
        var body = new byte[n]; await stream.ReadExactlyAsync(body);
        if (System.Text.Encoding.ASCII.GetString(body) != "request") throw new Exception("Frame payload corrupt");
        await stream.ReadExactlyAsync(header);
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header) != 0) throw new Exception("Missing half-close frame");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, 5);
        await stream.WriteAsync(header); await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("reply"));
        Array.Clear(header); await stream.WriteAsync(header);
    });
    var transport = new TcpPhoneTransport("127.0.0.1", framePort);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var framed = new FramedUsbStream(await transport.ConnectAsync(timeout.Token));
    await framed.WriteAsync(System.Text.Encoding.ASCII.GetBytes("request"), timeout.Token);
    await framed.ShutdownWriteAsync();
    using var reply = new MemoryStream(); await framed.CopyToAsync(reply, timeout.Token);
    await server.WaitAsync(TimeSpan.FromSeconds(5));
    if (System.Text.Encoding.ASCII.GetString(reply.ToArray()) != "reply") throw new Exception("Framed USB lost response after half-close");
    Console.WriteLine("PASS: framed USB preserves response after client half-close");
} finally { frameListener.Stop(); }

using (var wire = new MemoryStream()) {
    await using var frameWriter = new FramedUsbStream(wire);
    var large = Enumerable.Range(0, 65537).Select(x => (byte)x).ToArray();
    await frameWriter.WriteAsync(large); await frameWriter.ShutdownWriteAsync();
    var bytes = wire.ToArray();
    if (bytes.Length != large.Length + 16 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0,4)) != 32768 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(32772,4)) != 32768 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(65544,4)) != 1) throw new Exception("Large USB writes were framed incorrectly");
    using var input = new MemoryStream(bytes); await using var frameReader = new FramedUsbStream(input); using var decoded = new MemoryStream();
    var small = new byte[17]; int n; while ((n = await frameReader.ReadAsync(small)) != 0) decoded.Write(small, 0, n);
    if (!large.SequenceEqual(decoded.ToArray())) throw new Exception("Fragmented USB frames lost or reordered data");
}
using (var invalid = new MemoryStream(new byte[] {0,0,128,1})) {
    await using var reader = new FramedUsbStream(invalid);
    try { await reader.ReadAsync(new byte[1]); throw new Exception("Oversized frame was accepted"); }
    catch (IOException) { }
}
Console.WriteLine("PASS: framed USB handles large partial reads and rejects oversized frames");

var listed = AdbDeviceCatalog.Parse("List of devices attached\nTAB123 device product:foo model:SM_X218U device:bar\nPHONE456 unauthorized usb:1-3\n");
if (listed.Length != 2 || listed[0].Model != "SM X218U") throw new Exception("ADB device listing did not preserve device states and model");
var choice = AdbDeviceCatalog.Select(listed, null);
if (!choice.Result.Ok || choice.Serial != "TAB123") throw new Exception("A ready device should remain usable when another is unauthorized");
choice = AdbDeviceCatalog.Select(listed, "PHONE456");
if (choice.Result.Ok || choice.Result.Code != "ADB_UNAUTHORIZED") throw new Exception("An explicitly unauthorized device must not be selected");
var twoReady = AdbDeviceCatalog.Parse("List of devices attached\nTAB123 device model:SM_X218U\nPHONE456 device model:CPH2647\n");
if (AdbDeviceCatalog.Select(twoReady, null).Result.Ok) throw new Exception("Multiple ready devices require explicit selection");
if (AdbDeviceCatalog.Select(twoReady, "PHONE456").Serial != "PHONE456") throw new Exception("Selected phone was not targeted");
if (AdbDeviceCatalog.Select(twoReady, "MISSING").Result.Ok) throw new Exception("Missing selected device must not silently switch devices");
if (AdbDeviceCatalog.Parse("--help device model:bad\n").Length != 0) throw new Exception("Unsafe device identifier accepted");
if (AdbDeviceCatalog.Parse("emulator-5554 device model:Emulator\n").Length != 1) throw new Exception("Valid hyphenated ADB identifiers must be accepted");
Console.WriteLine("PASS: Android device selection handles multiple, unauthorized, and missing devices safely");

var adbCommands = new List<string>();
var fakePackagePresent = false;
var fakeForwardPresent = true;
Task<(int ExitCode, string StdOut, string StdErr)> FakeAdb(string file, string command) {
    adbCommands.Add(command);
    if (command == "version") return Task.FromResult((0,"Android Debug Bridge", ""));
    if (command == "devices -l") return Task.FromResult((0,"List of devices attached\nTAB123 device model:SM_X218U\nPHONE456 device model:CPH2647\n", ""));
    if (command == "forward --list") return Task.FromResult((0, fakeForwardPresent ? "PHONE456 tcp:23456 tcp:10889\nTAB123 tcp:8739 tcp:8739\n" : "TAB123 tcp:8739 tcp:8739\n", ""));
    if (command == "-s PHONE456 forward tcp:0 tcp:10889") return Task.FromResult((0,"23456\n", ""));
    if (command == "-s PHONE456 shell dumpsys package org.tetherbridge.classicapp" && fakePackagePresent) return Task.FromResult((0,"  Package [org.tetherbridge.classicapp]\n    versionName=0.6.4\n", ""));
    if (command.StartsWith("-s PHONE456 shell content query")) return Task.FromResult((0,"Row: 0 payload=tetherbridge://pair?example=1\n", ""));
    if (command.StartsWith("-s PHONE456 ")) return Task.FromResult((0,"", ""));
    return Task.FromResult((1,"", "Unexpected or untargeted command"));
}
var selectedAdb = new AdbService("PHONE456", () => "fake-adb", FakeAdb);
if (!(await selectedAdb.CheckAsync()).Ok) throw new Exception("Selected ADB device check failed");
if (!(await selectedAdb.ReadUsbPairingPayloadAsync()).Result.Ok) throw new Exception("Selected-device pairing failed");
var missingPackage = await selectedAdb.QueryPackageVersionAsync();
if (missingPackage.Ok || missingPackage.Code != "ADB_PACKAGE_NOT_FOUND") throw new Exception("Missing Android package must not be reported as installed");
fakePackagePresent = true;
if (!(await selectedAdb.QueryPackageVersionAsync()).Ok) throw new Exception("Installed Android package should be recognized");
var selectedForward = await selectedAdb.AllocateForwardAsync(10889);
if (!selectedForward.Result.Ok || selectedForward.Port != 23456) throw new Exception("Selected-device forwarding failed");
if (!(await selectedAdb.RecoverUsbBridgeAsync(23456,10889, () => Task.FromResult(DiagnosticResult.Pass("OK", "Bridge healthy")))).Ok) throw new Exception("Selected-device recovery failed");
if (adbCommands.Contains("-s PHONE456 forward --no-rebind tcp:23456 tcp:10889")) throw new Exception("An existing owned forward should be reused without trying to replace it");
fakeForwardPresent = false;
if (!(await selectedAdb.RecoverUsbBridgeAsync(23456,10889, () => Task.FromResult(DiagnosticResult.Pass("OK", "Bridge healthy")))).Ok || !adbCommands.Contains("-s PHONE456 forward --no-rebind tcp:23456 tcp:10889")) throw new Exception("A missing forward should be recreated without rebinding another mapping");
fakeForwardPresent = true;
var failedRecovery = await selectedAdb.RecoverUsbBridgeAsync(23456,10889, () => Task.FromResult(DiagnosticResult.Fail("DOWN", "Bridge stopped", "health", "Start bridge")));
if (failedRecovery.Ok || failedRecovery.Code != "ADB_USB_BRIDGE_NOT_RUNNING" || !failedRecovery.Fix.Contains("Start USB Bridge")) throw new Exception("A forward alone must not be reported as successful bridge recovery");
if (!(await selectedAdb.ReleaseOwnedForwardAsync()).Ok) throw new Exception("Selected-device cleanup failed");
if (adbCommands.Any(c => c.StartsWith("shell ") || c.StartsWith("forward tcp:") || c.StartsWith("-s TAB123"))) throw new Exception("A device-specific command was untargeted or reached the other tablet");
var missingAdb = new AdbService("MISSING", () => "fake-adb", FakeAdb);
if ((await missingAdb.AllocateForwardAsync(10889)).Result.Ok) throw new Exception("Missing selected device should not fall back");
Console.WriteLine("PASS: pairing, recovery, forwarding and cleanup target the selected Android device");

var transitionActions = new List<string>();
var notReady = await ConnectionTransition.PrepareIfReadyAsync(
    () => Task.FromResult(DiagnosticResult.Fail("ADB_SELECTED_MISSING", "Missing", "test", "Reconnect")),
    () => { transitionActions.Add("stop existing"); return Task.CompletedTask; });
if (notReady.Ok || transitionActions.Count != 0) throw new Exception("A missing selected device must not disconnect the current bridge");
var readyTransition = await ConnectionTransition.PrepareIfReadyAsync(
    () => Task.FromResult(DiagnosticResult.Pass("ADB_OK", "Ready")),
    () => { transitionActions.Add("stop existing"); return Task.CompletedTask; });
if (!readyTransition.Ok || transitionActions.Count != 1) throw new Exception("A ready device must prepare the existing bridge");
Console.WriteLine("PASS: unavailable selected device leaves current bridge running");

var udpPacket = new byte[] {0,0,0,1,127,0,0,1,0,53,7,8,9};
if (!SocksUdpDatagram.TryParse(udpPacket, out var udpTarget, out var udpBody) || udpTarget is null || udpTarget.Authority != "127.0.0.1:53" || !udpBody.SequenceEqual(new byte[] {7,8,9})) throw new Exception("SOCKS UDP IPv4 datagram did not parse");
if (!SocksUdpDatagram.BuildReply(udpTarget, udpBody).SequenceEqual(udpPacket)) throw new Exception("SOCKS UDP reply did not preserve address and payload");
var domainPacket = new byte[] {0,0,0,3,3,(byte)'d',(byte)'n',(byte)'s',0,53,1};
if (!SocksUdpDatagram.TryParse(domainPacket, out var domainTarget, out _) || domainTarget?.Authority != "dns:53") throw new Exception("SOCKS UDP domain did not parse");
var ipv6Packet = new byte[] {0,0,0,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,53,1};
if (!SocksUdpDatagram.TryParse(ipv6Packet, out var v6Target, out _) || v6Target?.Authority != "[::1]:53") throw new Exception("SOCKS UDP IPv6 did not parse");
foreach (var bad in new[] { new byte[] {0,0,1,1,127,0,0,1,0,53}, new byte[] {0,1,0,1,127,0,0,1,0,53}, new byte[] {0,0,0,1,127}, new byte[] {0,0,0,1,127,0,0,1,0,0}, new byte[] {0,0,0,3,1,10,0,53} })
    if (SocksUdpDatagram.TryParse(bad, out _, out _)) throw new Exception("Malformed or fragmented SOCKS UDP datagram accepted");
Console.WriteLine("PASS: SOCKS UDP packet format validates IPv4, IPv6, domain and malformed packets");

var fakePhone = new TcpListener(IPAddress.Loopback, 0); fakePhone.Start();
var fakePhonePort = ((IPEndPoint)fakePhone.LocalEndpoint).Port;
var reserveUdp = new TcpListener(IPAddress.Loopback, 0); reserveUdp.Start(); var udpProxyPort = ((IPEndPoint)reserveUdp.LocalEndpoint).Port; reserveUdp.Stop();
var testUdpRelay = new LocalSocks5Relay(); testUdpRelay.Start(new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1",fakePhonePort),null),udpProxyPort,Console.WriteLine);
var fakePhoneTask = Task.Run(async () => {
    using var peer = await fakePhone.AcceptTcpClientAsync(); var s=peer.GetStream();
    try {
        var end=new byte[]{13,10,13,10}; var matched=0; var one=new byte[1];
        while(matched<4) { await s.ReadExactlyAsync(one); matched=one[0]==end[matched]?matched+1:0; }
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
        for(var i=0;i<3;i++) {
            var h=new byte[4]; await s.ReadExactlyAsync(h); var count=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(h);
            var data=new byte[count]; await s.ReadExactlyAsync(data); await s.WriteAsync(h); await s.WriteAsync(data);
        }
        var wait=new byte[1]; await s.ReadAsync(wait);
    } catch (IOException) { }
});
try {
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var ctrl=new TcpClient(); await ctrl.ConnectAsync(IPAddress.Loopback,udpProxyPort,timeout.Token); var tcp=ctrl.GetStream();
    await tcp.WriteAsync(new byte[]{5,1,0},timeout.Token); var auth=new byte[2]; await tcp.ReadExactlyAsync(auth,timeout.Token);
    await tcp.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0},timeout.Token); var reply=new byte[10]; await tcp.ReadExactlyAsync(reply,timeout.Token);
    if(reply[1]!=0) throw new Exception("UDP association rejected");
    var udpBind=new IPEndPoint(new IPAddress(reply.AsSpan(4,4)),(reply[8]<<8)|reply[9]);
    using var owner=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); using var intruder=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
    var udpHeader=new byte[]{0,0,0,1,127,0,0,1,0,53}; var invalidFragment=(byte[])udpHeader.Clone(); invalidFragment[2]=1;
    await intruder.SendAsync(invalidFragment,udpBind,timeout.Token);
    foreach(var body in new[]{new byte[]{1,2,3},new byte[40000],Array.Empty<byte>()}) {
        var packet=udpHeader.Concat(body).ToArray(); await owner.SendAsync(packet,udpBind,timeout.Token);
        var received=await owner.ReceiveAsync(timeout.Token); if(!received.Buffer.SequenceEqual(packet)) throw new Exception("UDP association damaged a datagram");
    }
    await intruder.SendAsync(udpHeader.Concat(new byte[]{9}).ToArray(),udpBind,timeout.Token);
    using(var shortWait=new CancellationTokenSource(TimeSpan.FromMilliseconds(300))) {
        try { await intruder.ReceiveAsync(shortWait.Token); throw new Exception("Another UDP source was accepted"); }
        catch (OperationCanceledException) { }
    }
    var udpStats = testUdpRelay.GetSnapshot();
    if (udpStats.BytesClientToPhone < 40003 || udpStats.BytesPhoneToClient < 40003) throw new Exception("UDP traffic is missing from relay counters");
    if (udpStats.UnsupportedUdpRequests < 2) throw new Exception("Rejected UDP datagrams are missing from relay counters");
    ctrl.Close();
    var until=DateTime.UtcNow.AddSeconds(5); var released=false;
    while(DateTime.UtcNow<until) {
        try { using var rebind=new UdpClient(udpBind); released=true; break; }
        catch(SocketException) { await Task.Delay(50); }
    }
    if(!released) throw new Exception("UDP association did not close with its control connection");
    await fakePhoneTask.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: UDP association preserves datagrams, pins its source and cleans up on disconnect");
} finally { testUdpRelay.Stop(); fakePhone.Stop(); }

if (!PhoneCapabilities.SupportsUdp("{\"capabilities\":[\"tcp\",\"udp\"]}")) throw new Exception("UDP-capable Android status was not recognized");
if (PhoneCapabilities.SupportsUdp("{\"version\":\"0.6.4\"}") || PhoneCapabilities.SupportsUdp("invalid") || PhoneCapabilities.SupportsUdp("{\"capabilities\":\"udp\"}")) throw new Exception("Missing or invalid UDP capability was accepted");
Console.WriteLine("PASS: Android UDP capability negotiation rejects old or invalid status");

var blockedReserve = new TcpListener(IPAddress.Loopback, 0); blockedReserve.Start(); var blockedPort = ((IPEndPoint)blockedReserve.LocalEndpoint).Port; blockedReserve.Stop();
var blockedRelay = new LocalSocks5Relay(); blockedRelay.Start(new PhoneBridgeSession(new UnusedTransport(), null), blockedPort, _ => {}, allowUdp: false);
try {
    using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, blockedPort); var stream=c.GetStream();
    await stream.WriteAsync(new byte[]{5,1,0}); var auth=new byte[2]; await stream.ReadExactlyAsync(auth).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    await stream.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0}); var reply=new byte[10]; await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    if (reply[1]!=7) throw new Exception("UDP must be rejected explicitly when the phone lacks UDP support");
    Console.WriteLine("PASS: older Android apps get an explicit unsupported UDP reply");
} finally { blockedRelay.Stop(); }

var dnsRequest = new byte[] {0x12,0x34,1,0,0,1,0,0,0,0,0,0,1,97,0,0,1,0,1};
var dnsResponse = (byte[])dnsRequest.Clone(); dnsResponse[2] |= 0x80;
if (!DnsMessageValidation.IsQuery(dnsRequest) || !DnsMessageValidation.MatchesResponse(dnsRequest,dnsResponse)) throw new Exception("Valid DNS request/response rejected");
var wrongId=(byte[])dnsResponse.Clone(); wrongId[1]++; if (DnsMessageValidation.MatchesResponse(dnsRequest,wrongId)) throw new Exception("Mismatched DNS response accepted");
var wrongQuestion=(byte[])dnsResponse.Clone(); wrongQuestion[13]=(byte)'b'; if (DnsMessageValidation.MatchesResponse(dnsRequest,wrongQuestion)) throw new Exception("Wrong DNS question accepted");
if (DnsMessageValidation.IsQuery(new byte[4]) || DnsMessageValidation.IsQuery(dnsResponse) || DnsMessageValidation.MatchesResponse(dnsRequest,dnsRequest)) throw new Exception("Invalid DNS message accepted");
Console.WriteLine("PASS: DNS responses must match the request ID and question");

var dnsProxy = new LocalDnsProxy();
dnsProxy.Start((q, _) => { var answer=(byte[])q.Clone(); answer[2]|=0x80; return Task.FromResult(answer); });
try {
    using var dnsClient = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
    await dnsClient.SendAsync(dnsRequest,new IPEndPoint(IPAddress.Loopback,dnsProxy.Port));
    using var dnsDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var received = await dnsClient.ReceiveAsync(dnsDeadline.Token);
    if (!DnsMessageValidation.MatchesResponse(dnsRequest,received.Buffer)) throw new Exception("Local DNS did not return a matching answer");
    using var dnsTcp = new TcpClient(); await dnsTcp.ConnectAsync(IPAddress.Loopback,dnsProxy.Port,dnsDeadline.Token);
    var dnsStream=dnsTcp.GetStream(); var dnsLength=new byte[] {(byte)(dnsRequest.Length>>8),(byte)dnsRequest.Length};
    await dnsStream.WriteAsync(dnsLength,dnsDeadline.Token); await dnsStream.WriteAsync(dnsRequest,dnsDeadline.Token);
    await dnsStream.ReadExactlyAsync(dnsLength,dnsDeadline.Token); var dnsReply=new byte[(dnsLength[0]<<8)|dnsLength[1]]; await dnsStream.ReadExactlyAsync(dnsReply,dnsDeadline.Token);
    if(!DnsMessageValidation.MatchesResponse(dnsRequest,dnsReply)) throw new Exception("DNS TCP response mismatch");
    Console.WriteLine("PASS: local DNS proxy supports UDP and TCP");
} finally { dnsProxy.Stop(); }

var dnsFakePhone = new TcpListener(IPAddress.Loopback,0); dnsFakePhone.Start();
var dnsFakePort=((IPEndPoint)dnsFakePhone.LocalEndpoint).Port;
var dnsReserve=new TcpListener(IPAddress.Loopback,0); dnsReserve.Start(); var dnsSocksPort=((IPEndPoint)dnsReserve.LocalEndpoint).Port; dnsReserve.Stop();
var dnsSocks=new LocalSocks5Relay(); dnsSocks.Start(new PhoneBridgeSession(new TcpPhoneTransport("127.0.0.1",dnsFakePort),null),dnsSocksPort,_=>{});
var dnsPhoneTask=Task.Run(async ()=> { using var peer=await dnsFakePhone.AcceptTcpClientAsync(); var stream=peer.GetStream(); var term=new byte[]{13,10,13,10}; var match=0; var one=new byte[1]; while(match<4){await stream.ReadExactlyAsync(one);match=one[0]==term[match]?match+1:0;} await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n")); var header=new byte[4];await stream.ReadExactlyAsync(header); var size=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header); var request=new byte[size];await stream.ReadExactlyAsync(request);request[2]|=0x80;await stream.WriteAsync(header);await stream.WriteAsync(request); });
try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var resolver=new SocksDnsResolver(dnsSocksPort,IPAddress.Parse("1.1.1.1")); var local=new LocalDnsProxy(); local.Start(resolver.ResolveAsync); try { using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); await client.SendAsync(dnsRequest,new IPEndPoint(IPAddress.Loopback,local.Port),timeout.Token); var answer=await client.ReceiveAsync(timeout.Token); if(!DnsMessageValidation.MatchesResponse(dnsRequest,answer.Buffer)) throw new Exception("DNS over SOCKS did not return matching response"); } finally { local.Stop(); } await dnsPhoneTask.WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine("PASS: local DNS request traverses SOCKS UDP and phone tunnel"); } finally {dnsSocks.Stop();dnsFakePhone.Stop();}

sealed class UnusedTransport : IPhoneTransport {
    public string DisplayName => "unused";
    public Task<Stream> ConnectAsync(CancellationToken token) => throw new Exception("Unexpected upstream connection");
    public Task<DiagnosticResult> ProbeHealthAsync(string? token) => throw new NotImplementedException();
    public Task<DiagnosticResult> ProbeStatusAsync(string? token) => throw new NotImplementedException();
}

sealed class FakeNetworkBackend : ITransactionalNetworkBackend {
    public Dictionary<string,string?> Values { get; } = new();
    public string? FailAfterWriteKey { get; set; }
    public string? ConflictAfterWriteKey { get; set; }
    public TaskCompletionSource? Block { get; set; }
    public TaskCompletionSource? Entered { get; set; }
    public async Task ApplyAsync(NetworkChange change, CancellationToken token) {
        if (!Values.TryGetValue(change.Key, out var value) || value != change.Before) throw new IOException("State changed before apply");
        Values[change.Key] = change.After;
        if (Block is not null) { Entered?.TrySetResult(); await Block.Task.WaitAsync(token); }
        if (change.Key == ConflictAfterWriteKey) { Values[change.Key] = "external"; throw new IOException("Simulated conflict after mutation"); }
        if (change.Key == FailAfterWriteKey) throw new IOException("Simulated interrupted write");
    }
    public Task<NetworkRecoveryResult> RecoverAsync(NetworkChange change, CancellationToken token) {
        Values.TryGetValue(change.Key, out var value);
        if (value == change.Before) return Task.FromResult(NetworkRecoveryResult.AlreadyRestored);
        if (value != change.After) return Task.FromResult(NetworkRecoveryResult.Conflict);
        Values[change.Key] = change.Before; return Task.FromResult(NetworkRecoveryResult.Restored);
    }
}

sealed class BlockingAfterContentStream(byte[] bytes) : Stream {
    private int offset;
    public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => offset; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        if (offset == bytes.Length) { await Task.Delay(Timeout.Infinite, token); return 0; }
        var count = Math.Min(3, Math.Min(buffer.Length, bytes.Length - offset)); bytes.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
    }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

sealed class TrackedReadStream(byte[] bytes) : MemoryStream(bytes) { public bool WasDisposed {get;private set;} protected override void Dispose(bool disposing) { if(disposing) WasDisposed=true; base.Dispose(disposing); } }
sealed class SingleStreamTransport(Stream stream) : IPhoneTransport {
    public string DisplayName => "test";
    public Task<Stream> ConnectAsync(CancellationToken token) => Task.FromResult(stream);
    public Task<DiagnosticResult> ProbeHealthAsync(string? token) => throw new NotSupportedException();
    public Task<DiagnosticResult> ProbeStatusAsync(string? token) => throw new NotSupportedException();
}

sealed class FakeMonotonicClock : TimeProvider { private long ticks; public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => ticks; public void Advance(TimeSpan duration) => ticks += duration.Ticks; }

sealed class FakeRouteRunner : IRouteCommandRunner {
    public Action<string[]>? AfterMutation {get;set;}
    public string? IgnoreAdd {get;set;}
    public string? IgnoreDelete {get;set;}
    public List<string[]> Calls { get; } = new(); public int FailCall {get;set;} public bool Preexisting {get;set;} public HashSet<int> FailCalls { get; } = new();
    private readonly HashSet<string> owned = new();
    public Task<PrivateRouteState> InspectAsync(int index,string prefix,bool ipv6,CancellationToken token) => Task.FromResult(Preexisting ? PrivateRouteState.Conflict : owned.Contains(prefix) ? PrivateRouteState.Owned : PrivateRouteState.Absent);
    public Task PrepareAsync(int index,string prefix,bool ipv6,CancellationToken token) => Task.CompletedTask;
    public Task RunAsync(string[] args, CancellationToken token) {
        Calls.Add(args); if(args[2]=="add" && args[4]!=IgnoreAdd)owned.Add(args[4]); else if(args[2]=="delete" && args[4]!=IgnoreDelete)owned.Remove(args[4]); AfterMutation?.Invoke(args);
        if(Calls.Count==FailCall || FailCalls.Contains(Calls.Count))throw new IOException("Simulated netsh failure after mutation"); return Task.CompletedTask;
    }
}

sealed class FakeTunnelRuntime : ITunnelRuntime {
    public List<string> Events {get;} = new(); public TaskCompletionSource Renewed {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool FailDuringRoutes {get;set;} public bool RefuseRemoval {get;set;}
    public Task PreflightAsync(CancellationToken token){Events.Add("preflight");return Task.CompletedTask;}
    public Task StartEngineAsync(CancellationToken token){Events.Add("engine");return Task.CompletedTask;}
    public Task ConfigureAdapterAsync(CancellationToken token){Events.Add("configure");return Task.CompletedTask;}
    public Task StartRoutesAsync(CancellationToken token){Events.Add("routes");if(FailDuringRoutes)throw new IOException("route failure");return Task.CompletedTask;}
    public Task RenewRoutesAsync(CancellationToken token){Events.Add("renew");Renewed.TrySetResult();return Task.CompletedTask;}
    public Task<bool> RemoveRoutesAsync(CancellationToken token){Events.Add("remove-routes");return Task.FromResult(!RefuseRemoval);}
    public Task StopEngineAsync(CancellationToken token){Events.Add("stop-engine");return Task.CompletedTask;}
}
