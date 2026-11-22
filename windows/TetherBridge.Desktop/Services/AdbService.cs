using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TetherBridge.Desktop.Services;

public sealed class AdbService
{
    private const string UsbPairingProviderUri = "content://org.tetherbridge.classicapp.usbpairing/pairing";
    private static readonly TimeSpan AdbCommandTimeout = TimeSpan.FromSeconds(10);
    private (string Serial, int LocalPort, int PhonePort)? _ownedForward;
    private readonly string? _requestedSerial;
    private string? _resolvedSerial;
    private readonly Func<string?> _resolveAdb;
    private readonly Func<string, string, Task<(int ExitCode, string StdOut, string StdErr)>> _run;

    public AdbService(string? selectedSerial = null, Func<string?>? resolver = null,
        Func<string, string, Task<(int ExitCode, string StdOut, string StdErr)>>? runner = null)
    {
        _requestedSerial = selectedSerial;
        _resolveAdb = resolver ?? ResolveAdbExecutable;
        _run = runner ?? ((file, args) => RunAsync(file, args));
    }

    public async Task<(DiagnosticResult Result, AdbDevice[] Devices)> ListDevicesAsync()
    {
        var adb = _resolveAdb();
        if (adb is null) return (DiagnosticResult.Fail("ADB_NOT_FOUND", "ADB was not found.", "adb resolver", "Install Android platform-tools."), Array.Empty<AdbDevice>());
        var result = await _run(adb, "devices -l");
        if (result.ExitCode != 0) return (DiagnosticResult.Fail("ADB_FAILED", "Could not list Android devices.", "adb devices -l", CleanAdbError(result.StdErr)), Array.Empty<AdbDevice>());
        return (DiagnosticResult.Pass("ADB_DEVICES_LISTED", "Android device list refreshed."), AdbDeviceCatalog.Parse(result.StdOut));
    }

    private async Task<DiagnosticResult> SelectDeviceAsync()
    {
        var (result, devices) = await ListDevicesAsync();
        if (!result.Ok) return result;
        var selection = AdbDeviceCatalog.Select(devices, _requestedSerial);
        _resolvedSerial = selection.Serial;
        return selection.Result;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunOnSelectedAsync(string adb, string args)
    {
        if (_resolvedSerial is null)
        {
            var selection = await SelectDeviceAsync();
            if (!selection.Ok) return (-1, "", selection.Message + " " + selection.Fix);
        }
        return await _run(adb, $"-s {_resolvedSerial} {args}");
    }

    public async Task<DiagnosticResult> CheckAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        var version = await _run(adb, "version");
        if (version.ExitCode != 0)
            return DiagnosticResult.Fail("ADB_FAILED", "ADB was found but could not run.", adb + " version", version.StdErr.Trim());

        return await SelectDeviceAsync();
    }

    public async Task<DiagnosticResult> ForwardAsync(int localPort, int phonePort)
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        if (_ownedForward is { } owned && owned.LocalPort == localPort && owned.PhonePort == phonePort)
        {
            var list = await _run(adb, "forward --list");
            if (list.ExitCode == 0 && AdbForwardAllocator.IsOwnedMapping(list.StdOut, owned.Serial, localPort, phonePort))
                return DiagnosticResult.Pass("ADB_FORWARD_PRESENT", "The session's USB forward is already present.", "adb forward --list");
        }

        var result = await RunOnSelectedAsync(adb, $"forward --no-rebind tcp:{localPort} tcp:{phonePort}");
        return result.ExitCode == 0
            ? DiagnosticResult.Pass("ADB_FORWARD_OK", $"Forwarded local TCP {localPort} to phone TCP {phonePort}.", "adb forward")
            : DiagnosticResult.Fail("ADB_FORWARD_FAILED", "USB port forwarding could not be created.", "adb forward", result.StdErr.Trim());
    }

    public async Task<(DiagnosticResult Result, int Port)> AllocateForwardAsync(int phonePort)
    {
        var adb = _resolveAdb();
        if (adb is null)
            return (DiagnosticResult.Fail("ADB_NOT_FOUND", "ADB is not installed, bundled, or discoverable in the Android SDK.", "adb resolver", "Install Android platform-tools."), 0);
        var selected = await SelectDeviceAsync();
        if (!selected.Ok || _resolvedSerial is null) return (selected, 0);
        var serial = _resolvedSerial;
        var allocation = await AdbForwardAllocator.CreateAsync(phonePort, args => _run(adb, $"-s {serial} {args}"));
        if (allocation.Result.Ok) _ownedForward = (serial, allocation.Port, phonePort);
        return allocation;
    }

    public async Task<DiagnosticResult> ReleaseOwnedForwardAsync()
    {
        var owned = _ownedForward;
        _ownedForward = null;
        if (owned is null) return DiagnosticResult.Pass("ADB_FORWARD_NONE", "No owned USB forward to remove.");
        var adb = _resolveAdb();
        if (adb is null) return DiagnosticResult.Fail("ADB_NOT_FOUND", "Could not remove the USB forward.", "adb resolver", "Install Android platform-tools.");
        var list = await _run(adb, "forward --list");
        if (list.ExitCode != 0) return DiagnosticResult.Fail("ADB_FORWARD_LIST_FAILED", "Could not check the owned USB forward.", "adb forward --list", CleanAdbError(list.StdErr));
        var mapping = owned.Value;
        if (!AdbForwardAllocator.IsOwnedMapping(list.StdOut, mapping.Serial, mapping.LocalPort, mapping.PhonePort))
            return DiagnosticResult.Pass("ADB_FORWARD_GONE", "The owned USB forward is no longer present; nothing was removed.");
        var result = await _run(adb, $"-s {mapping.Serial} forward --remove tcp:{mapping.LocalPort}");
        return result.ExitCode == 0
            ? DiagnosticResult.Pass("ADB_FORWARD_REMOVED", "Removed the USB forward created by this session.")
            : DiagnosticResult.Fail("ADB_FORWARD_REMOVE_FAILED", "Could not remove the USB forward created by this session.", "adb forward --remove", CleanAdbError(result.StdErr));
    }

    public async Task<DiagnosticResult> ForwardListAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        var result = await _run(adb, "forward --list");
        if (result.ExitCode != 0)
            return DiagnosticResult.Fail("ADB_FORWARD_LIST_FAILED", "Could not list current ADB forwards.", "adb forward --list", CleanAdbError(result.StdErr));

        var summary = string.IsNullOrWhiteSpace(result.StdOut) ? "(no active forwards)" : CollapseLines(result.StdOut, 6);
        return DiagnosticResult.Pass("ADB_FORWARD_LIST", "Current ADB forwards: " + summary, "adb forward --list");
    }

    public async Task<DiagnosticResult> WakeAndDismissKeyguardAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        var wake = await RunOnSelectedAsync(adb, "shell input keyevent KEYCODE_WAKEUP");
        var dismiss = await RunOnSelectedAsync(adb, "shell wm dismiss-keyguard");
        if (wake.ExitCode == 0 && dismiss.ExitCode == 0)
            return DiagnosticResult.Pass("ADB_WAKE_OK", "Sent wake and dismiss-keyguard commands to the phone.", "adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard");

        var detail = string.Join("; ", new[] { CleanAdbError(wake.StdErr), CleanAdbError(dismiss.StdErr) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return DiagnosticResult.Fail("ADB_WAKE_FAILED", "Could not wake or dismiss keyguard over ADB.", "adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard", detail);
    }

    public async Task<DiagnosticResult> LaunchAndroidAppAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        const string command = "shell am start -n org.tetherbridge.classicapp/.MainActivity";
        var result = await RunOnSelectedAsync(adb, command);
        return result.ExitCode == 0
            ? DiagnosticResult.Pass("ADB_APP_LAUNCH_OK", "Requested TetherBridge launch on the phone over USB.", "adb " + command)
            : DiagnosticResult.Fail("ADB_APP_LAUNCH_FAILED", "Could not launch TetherBridge on the phone over USB.", "adb " + command, CleanAdbError(result.StdErr));
    }

    public async Task<DiagnosticResult> QueryPackageVersionAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return DiagnosticResult.Fail(
                "ADB_NOT_FOUND",
                "ADB is not installed, bundled, or discoverable in the Android SDK.",
                "adb resolver",
                "Install Android platform-tools or place platform-tools\\adb.exe beside the companion.");

        const string command = "shell dumpsys package org.tetherbridge.classicapp";
        var result = await RunOnSelectedAsync(adb, command);
        if (result.ExitCode != 0)
            return DiagnosticResult.Fail("ADB_PACKAGE_QUERY_FAILED", "Could not query Android package metadata.", "adb " + command, CleanAdbError(result.StdErr));

        var lines = result.StdOut
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("versionName=", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("versionCode=", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("Package [org.tetherbridge.classicapp]", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();

        if (lines.Length == 0)
            return DiagnosticResult.Fail("ADB_PACKAGE_NOT_FOUND", "TetherBridge was not found on the selected Android device.", "adb " + command, "Install the matching Android app and try again.");

        var summary = string.Join("; ", lines);
        return DiagnosticResult.Pass("ADB_PACKAGE_VERSION", "Android package metadata: " + summary, "adb " + command);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> CollectEvidenceAsync()
    {
        var results = new List<DiagnosticResult>
        {
            await CheckAsync(),
            await ForwardListAsync(),
            await QueryPackageVersionAsync()
        };
        return results;
    }

    public async Task<DiagnosticResult> RecoverUsbBridgeAsync(int localPort, int phonePort, Func<Task<DiagnosticResult>> probeHealth)
    {
        var wake = await WakeAndDismissKeyguardAsync();
        var launch = await LaunchAndroidAppAsync();
        var forward = await ForwardAsync(localPort, phonePort);
        var details = $"wake={wake.Code}:{wake.Ok}; launch={launch.Code}:{launch.Ok}; forward={forward.Code}:{forward.Ok}";

        if (!forward.Ok)
            return DiagnosticResult.Fail("ADB_USB_RECOVERY_FAILED", "USB recovery could not restore the ADB forward. " + details, "adb wake; adb am start; adb forward", forward.Fix);
        DiagnosticResult health;
        try { health = await probeHealth(); }
        catch (Exception ex)
        {
            return DiagnosticResult.Fail("ADB_USB_RECOVERY_UNVERIFIED", "USB forward is ready but the bridge health check failed: " + ex.Message + "; " + details,
                "adb forward; bridge health", "Open TetherBridge on the selected Android device and tap Start USB Bridge.");
        }
        if (!health.Ok)
            return DiagnosticResult.Fail("ADB_USB_BRIDGE_NOT_RUNNING", "USB forward is ready but the Android bridge did not answer. " + details + "; " + health.Message,
                "adb forward; bridge health", "Open TetherBridge on the selected Android device and tap Start USB Bridge. Check notifications and battery restrictions if it stops again.");
        return DiagnosticResult.Pass("ADB_USB_RECOVERY_OK", "USB bridge answered the health check. " + details, "adb wake; adb am start; adb forward; bridge health");
    }

    public async Task<(DiagnosticResult Result, string Payload)> ReadUsbPairingPayloadAsync()
    {
        var adb = _resolveAdb();
        if (adb is null)
            return (
                DiagnosticResult.Fail(
                    "ADB_NOT_FOUND",
                    "ADB is not installed, bundled, or discoverable in the Android SDK.",
                    "adb resolver",
                    "Install Android platform-tools or place platform-tools\\adb.exe beside the companion."),
                "");

        var command = $"shell content query --uri {UsbPairingProviderUri} --projection payload";
        var result = await RunOnSelectedAsync(adb, command);
        if (result.ExitCode != 0)
            return (
                DiagnosticResult.Fail(
                    "USB_PAIRING_INJECT_FAILED",
                    "Could not read the Android pairing payload over USB.",
                    "adb " + command,
                    CleanAdbError(result.StdErr)),
                "");

        var payload = ExtractContentQueryValue(result.StdOut, "payload");
        if (string.IsNullOrWhiteSpace(payload))
            return (
                DiagnosticResult.Fail(
                    "USB_PAIRING_INJECT_EMPTY",
                    "Android returned no pairing payload.",
                    "adb " + command,
                    "Open the Android app once, then retry Inject from USB phone."),
                "");

        return (
            DiagnosticResult.Pass(
                "USB_PAIRING_INJECT_OK",
                "Read pairing payload from the phone over USB.",
                "adb " + command),
            payload.Trim());
    }

    private static string? ExtractContentQueryValue(string output, string fieldName)
    {
        var marker = fieldName + "=";
        var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        index += marker.Length;
        var end = output.IndexOf('\n', index);
        if (end < 0) end = output.Length;

        var value = output[index..end].Trim();
        var nextField = value.IndexOf(", ", StringComparison.Ordinal);
        if (nextField >= 0) value = value[..nextField].Trim();
        return value;
    }

    private static string CleanAdbError(string stderr)
    {
        var trimmed = stderr.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? "Install/open TetherBridge on the phone, authorize USB debugging, then retry."
            : trimmed;
    }

    private static string CollapseLines(string text, int maxLines) =>
        string.Join("; ", text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(maxLines));

    private static string? ResolveAdbExecutable()
    {
        foreach (var candidate in AdbCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> AdbCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "platform-tools", "adb.exe");
        yield return Path.Combine(baseDirectory, "adb.exe");

        foreach (var sdkRoot in SdkRootCandidates())
            yield return Path.Combine(sdkRoot, "platform-tools", "adb.exe");

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(pathEntry.Trim(), "adb.exe");
    }

    private static IEnumerable<string> SdkRootCandidates()
    {
        foreach (var variableName in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Android", "Sdk");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string file, string args, TimeSpan? timeout = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            using var timeoutCts = new CancellationTokenSource(timeout ?? AdbCommandTimeout);
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup after a hung adb command.
                }
                return (-2, "", $"Timed out after {(timeout ?? AdbCommandTimeout).TotalSeconds:N0}s: adb {args}");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
