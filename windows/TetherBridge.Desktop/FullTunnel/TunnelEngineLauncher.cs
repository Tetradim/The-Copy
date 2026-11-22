using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TetherBridge.Desktop.FullTunnel;

public sealed class TunnelEngine(Process process, ChildProcessJob job, Guid adapterId) : IDisposable
{
    public Process Process { get; } = process;
    public Guid AdapterId { get; } = adapterId;
    public async Task StopAsync(CancellationToken token)
    {
        job.Dispose();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await Process.WaitForExitAsync(timeout.Token);
        while (NetworkInterface.GetAllNetworkInterfaces().Any(item => Guid.TryParse(item.Id, out var id) && id == AdapterId))
            await Task.Delay(250, timeout.Token);
    }
    public void Dispose() { job.Dispose(); Process.Dispose(); }
}

public static class TunnelEngineLauncher
{
    private const string EngineHash = "076B3C3D6A372BAE3F49F2B415A4105F70C30A3ED3CAAED7979390E649892559";
    private const string WintunHash = "E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE";

    public static string[] BuildArguments(string adapterName)
    {
        if (!Regex.IsMatch(adapterName, "^TetherBridge-[0-9a-f]{32}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid private adapter name.");
        return ["--device", "tun://" + adapterName, "--proxy", "socks5://127.0.0.1:10810", "--mtu", "1500", "--loglevel", "warn"];
    }

    // This starts a driver-backed adapter but does not add routes or change DNS.
    // It is deliberately not connected to the normal desktop UI pending isolated validation.
    public static async Task<TunnelEngine> StartAsync(CancellationToken token)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TetherBridge", "Tunnel");
        CheckNoReparsePoints(directory);
        var exe = Path.Combine(directory, "tun2socks.exe"); var dll = Path.Combine(directory, "wintun.dll");
        TunnelPathTrust.Verify(directory, exe, dll);
        using var exeFile = OpenPinned(exe, EngineHash);
        using var dllFile = OpenPinned(dll, WintunHash);
        var adapterName = "TetherBridge-" + Guid.NewGuid().ToString("N");
        if (NetworkInterface.GetAllNetworkInterfaces().Any(item => item.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase))) throw new IOException("Private adapter name already exists.");
        var job = new ChildProcessJob(); Process? process = null;
        try
        {
            process = job.StartSuspendedThenResume(exe, BuildArguments(adapterName), directory, restrictedEnvironment: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException($"Tunnel engine exited with code {process.ExitCode}.");
                var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item => item.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
                if (adapter is not null && Guid.TryParse(adapter.Id, out var id)) return new TunnelEngine(process, job, id);
                await Task.Delay(250, timeout.Token);
            }
        }
        catch { job.Dispose(); process?.Dispose(); throw; }
    }
    private static FileStream OpenPinned(string path, string expected)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Tunnel dependency is a reparse point.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(file));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected))) throw new InvalidDataException("Tunnel dependency hash mismatch.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static void CheckNoReparsePoints(string directory)
    {
        for (var path = new DirectoryInfo(directory); path is not null; path = path.Parent)
            if (path.Exists && (path.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Tunnel installation path contains a reparse point.");
    }
}
