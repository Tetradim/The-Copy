using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TetherBridge.Desktop.FullTunnel;

internal static class LabUsbBenchmark
{
    public static async Task RunAsync(Uri origin, int mib, int workers, int? socksPort, int? expectedIndex)
    {
        if (!IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) || origin.UserInfo.Length != 0 ||
            origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("Supply an HTTP or HTTPS origin with a numeric IP address.");
        if (mib is < 1 or > 32 || workers is < 1 or > 4)
            throw new ArgumentException("Use 1–32 MiB per stream and 1–4 streams.");
        VerifyRoute();

        using var handler = new SocketsHttpHandler
        {
            UseProxy = socksPort.HasValue,
            Proxy = socksPort.HasValue ? new WebProxy($"socks5://127.0.0.1:{socksPort.Value}") : null,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
        using var period = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = period.Token;
        using var process = Process.GetCurrentProcess();
        var bytes = mib * 1024 * 1024;
        var initialCpu = process.TotalProcessorTime;
        var idle = new List<double>();
        for (var sample = 0; sample < 10; sample++)
            idle.Add(await PingAsync(client, token));

        var results = new List<object>();
        foreach (var concurrency in new[] { 1, workers }.Distinct())
        {
            foreach (var upload in new[] { false, true })
            {
                VerifyRoute();
                using var probes = CancellationTokenSource.CreateLinkedTokenSource(token);
                var clock = Stopwatch.StartNew();
                var transfers = Task.WhenAll(Enumerable.Range(0, concurrency)
                    .Select(_ => TransferAsync(client, bytes, upload, token)));
                var loaded = SampleAsync(client, probes.Token);
                double seconds;
                try
                {
                    await transfers;
                    seconds = clock.Elapsed.TotalSeconds;
                }
                finally { probes.Cancel(); }
                var samples = await loaded;
                VerifyRoute();
                results.Add(new
                {
                    direction = upload ? "upload" : "download", streams = concurrency,
                    transferredBytes = (long)bytes * concurrency,
                    seconds = Math.Round(seconds, 3),
                    megabitsPerSecond = Math.Round(bytes * (double)concurrency * 8 / seconds / 1_000_000, 2),
                    loadedLatencySamples = samples.Count,
                    loadedLatencyP95Ms = samples.Count >= 5 ? Percentile95(samples) : null
                });
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            path = socksPort.HasValue ? "SOCKS5" : "direct",
            expectedWindowsInterface = expectedIndex,
            idleLatencySamples = idle.Count,
            idleLatencyP95Ms = Percentile95(idle),
            benchmarkProcessCpuMs = (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
            transfers = results
        }, new JsonSerializerOptions { WriteIndented = true }));

        void VerifyRoute()
        {
            if (!socksPort.HasValue && !IPAddress.IsLoopback(address) && !expectedIndex.HasValue)
                throw new ArgumentException("Direct non-loopback runs require the expected Windows interface index.");
            if (expectedIndex.HasValue && WindowsRouteTable.GetPreferredInterface(address) != expectedIndex.Value)
                throw new IOException("Windows prefers an unexpected interface. No comparison result was recorded.");
        }
    }

    public static async Task MeasureRecoveryAsync(Uri origin, int socksPort)
    {
        if (origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Supply the benchmark server's HTTP or HTTPS origin.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var clock = Stopwatch.StartNew();
        await ProbeOnceAsync();
        Console.WriteLine("Baseline passed. Disconnect and reconnect the USB cable when ready. Waiting for an outage and recovery.");
        TimeSpan? unavailableAt = null;
        while (true)
        {
            await Task.Delay(500, deadline.Token);
            try
            {
                await ProbeOnceAsync();
                if (unavailableAt is not null)
                {
                    Console.WriteLine($"First successful request after the observed outage: {(clock.Elapsed - unavailableAt.Value).TotalSeconds:F2} seconds.");
                    return;
                }
            }
            catch (Exception error) when (!deadline.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
            {
                if (unavailableAt is null)
                {
                    unavailableAt = clock.Elapsed;
                    Console.WriteLine("Outage observed. Waiting for a fresh request to succeed.");
                }
            }
        }

        async Task ProbeOnceAsync()
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"), UseProxy = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };
            await PingAsync(client, deadline.Token);
        }
    }

    private static async Task TransferAsync(HttpClient client, int bytes, bool upload, CancellationToken token)
    {
        if (upload)
        {
            using var content = new ByteArrayContent(new byte[bytes]);
            using var response = await client.PostAsync("upload", content, token);
            response.EnsureSuccessStatusCode();
            if (!int.TryParse(await response.Content.ReadAsStringAsync(token), out var received) || received != bytes)
                throw new IOException("The benchmark server did not acknowledge the expected upload size.");
            return;
        }

        using var download = await client.GetAsync($"download?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, token);
        download.EnsureSuccessStatusCode();
        if (download.Content.Headers.ContentLength != bytes)
            throw new IOException("The benchmark server did not advertise the expected download size.");
        await using var stream = await download.Content.ReadAsStreamAsync(token);
        var buffer = new byte[64 * 1024];
        var total = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            total += read;
            if (total > bytes) throw new IOException("The response exceeded the requested benchmark size.");
        }
        if (total != bytes) throw new IOException("The downloaded byte count was incomplete.");
    }

    private static async Task<double> PingAsync(HttpClient client, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync("ping", token);
        response.EnsureSuccessStatusCode();
        return clock.Elapsed.TotalMilliseconds;
    }

    private static async Task<List<double>> SampleAsync(HttpClient client, CancellationToken token)
    {
        var samples = new List<double>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                samples.Add(await PingAsync(client, token));
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return samples;
    }

    private static double? Percentile95(List<double> samples)
    {
        if (samples.Count == 0) return null;
        samples.Sort();
        return Math.Round(samples[(int)Math.Ceiling(samples.Count * 0.95) - 1], 2);
    }
}
