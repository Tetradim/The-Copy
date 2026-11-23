using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TetherBridge.Desktop.Services;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop;

public sealed class MainForm : Form
{
    private const int LocalHttpProxyPort = 10809;
    private const int LocalSocks5Port = 10810;
    private const int SessionMonitorFailureThreshold = 6;

    private readonly TextBox _log = new() { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _diagnosticsBox = new() { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _pairingPayload = new() { Multiline = true, Width = 640, Height = 70, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _phoneIp = new() { Text = "192.168.43.1", Width = 150 };
    private readonly TextBox _phonePort = new() { Text = "10888", Width = 70 };
    private readonly TextBox _token = new() { Width = 260 };
    private readonly TextBox _fingerprint = new() { Width = 600 };
    private readonly ComboBox _bluetoothDevice = new() { Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _connectionType = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Connection type" };
    private readonly Dictionary<string, PairingPayload> _bluetoothPairings = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _bluetoothPairingPayload = new() { Width = 640, AccessibleName = "Android Bluetooth pairing details" };
    private readonly ComboBox _usbDevice = new() { Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _connectionState = new() { Width = 780, Height = 48, Text = "Disconnected" };
    private readonly Label _localStats = new() { Width = 780, Height = 52, Text = "Local relay: not running" };
    private readonly Label _socksStats = new() { Width = 780, Height = 52, Text = "SOCKS5 relay: not running" };
    private readonly Label _phoneStats = new() { Width = 780, Height = 92, Text = "Phone status: no active session" };
    private readonly Label _modeGuide = new() { Width = 780, Height = 84 };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _usbScanTimer = new() { Interval = 5000 };
    private readonly UsbHotplugDetector _usbHotplug = new();
    private bool _usbScanInFlight;
    private readonly LocalRelayProxy _relay = new();
    private readonly LocalSocks5Relay _socksRelay = new();
    private readonly LocalDnsProxy _dnsProxy = new();
    private readonly TextBox _dnsUpstream = new() { Text = "1.1.1.1", Width = 180 };
    private readonly NumericUpDown _bandwidthLimit = new() { Minimum = 0, Maximum = 100000, Width = 100 };
    private readonly TextBox _dnsPort = new() { Text = "1053", Width = 90 };
    private readonly Label _dnsState = new() { Text = "DNS proxy stopped", Width = 650, Height = 30 };
    private bool _udpAvailable;
    private readonly ProxyProfileStore _profiles = new();
    private readonly ConnectionActionGate _actionGate = new();
    private readonly DesktopSettingsStore _settings = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TetherBridge", "settings.json"));
    private readonly NotifyIcon _tray = new();
    private readonly CheckBox _autoConnectUsb = new() { Text = "Automatically connect USB at startup and when plugged in", AutoSize = true };
    private readonly CheckBox _autoReconnectBluetooth = new() { Text = "Reconnect this Bluetooth bridge after a drop", AutoSize = true, Checked = true };
    private bool _exitRequested;
    private readonly RecentLogBuffer _logLines = new();
    private PhoneBridgeSession? _session;
    private AdbService? _usbAdb;
    private readonly HashSet<AdbService> _pendingUsbCleanup = new();
    private CancellationTokenSource? _sessionMonitorCts;
    private bool _restoringAfterSessionLoss;
    private readonly CloseLifecycle _closeLifecycle = new();
    private bool _statusPollInFlight;

    public MainForm()
    {
        Text = $"TetherBridge Classic {Application.ProductVersion}";
        Size = new Size(920, 720);
        StartPosition = FormStartPosition.CenterScreen;
        _pollTimer.Tick += async (_, _) => await PollStatusAsync(silent: true);
        _usbScanTimer.Tick += async (_, _) => await ScanForUsbHotplugAsync();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        Controls.Add(tabs);
        _usbDevice.Items.Add("Automatic (one authorized device)");
        _usbDevice.SelectedIndex = 0;
        _autoConnectUsb.Checked = _settings.Load().AutoConnectUsb;
        _autoConnectUsb.CheckedChanged += (_, _) =>
        {
            try { _settings.Save(new DesktopSettings(_autoConnectUsb.Checked)); }
            catch (Exception ex) { Log("SETTINGS_SAVE_FAILED: " + ex.Message); }
        };
        ConfigureTray();
        Shown += async (_, _) =>
        {
            TryRecoverAbandonedProxy();
            await RefreshUsbDevicesAsync();
            if (_autoConnectUsb.Checked && !_profiles.HasActiveSnapshot && !WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
            {
                try { await _actionGate.RunAsync(async () =>
                {
                    var selection = SelectedUsbSerial;
                    bool Eligible() => !IsDisposed && _autoConnectUsb.Checked && !_exitRequested && !_profiles.HasActiveSnapshot &&
                        SelectedUsbSerial == selection && !WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort);
                    if (_session is not null || !Eligible()) return;
                    var (result, devices) = await new AdbService().ListDevicesAsync();
                    if (!result.Ok || !Eligible()) return;
                    var (selectedResult, candidate) = AdbDeviceCatalog.Select(devices, selection);
                    if (selectedResult.Ok && candidate is not null) await StartUsbAsync(Eligible, candidate);
                }); }
                catch (Exception ex) { Log("AUTO_CONNECT_FAILED: " + ex.Message); }
            }
        };

        UpdateModeGuide();
        _pollTimer.Start();
        _usbScanTimer.Start();
        Log($"READY: TetherBridge Classic {Application.ProductVersion} loaded. Use USB Bridge first for the most reliable proxy-mode path.");
    }

    private void ConfigureTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TetherBridge", null, (_, _) => ShowFromTray());
        menu.Items.Add("Connect selected mode", null, async (_, _) => { try { await _actionGate.RunAsync(ConnectSelectedAsync); } catch (Exception ex) { ReportTrayFailure("Connection failed", ex); } });
        menu.Items.Add("Disconnect / restore proxy", null, async (_, _) => { try { await _actionGate.RunAsync(RestoreProxyAsync); } catch (Exception ex) { ReportTrayFailure("Disconnect failed", ex); } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "TetherBridge";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    private void ReportTrayFailure(string title, Exception error)
    {
        Log(title + ": " + error.Message);
        if (!Visible && _tray.Visible)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = "Open TetherBridge to review the connection log.";
            _tray.ShowBalloonTip(5000);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void TryRecoverAbandonedProxy()
    {
        try
        {
            // A listening service may belong to another program. Never replace its proxy settings.
            var listenerExists = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(endpoint => endpoint.Port == LocalHttpProxyPort);
            var current = WindowsProxySettings.ReadCurrent();
            if (ProxyStartupRecovery.NeedsReview(current, listenerExists, _profiles.HasActiveSnapshot))
            {
                _connectionState.Text = "State: previous proxy session may need restoration";
                Log("STARTUP_PROXY_REVIEW: A prior session may have left a proxy behind. Review Windows proxy settings, then use Disable / Restore Proxy if appropriate.");
            }
            else if (_profiles.HasActiveSnapshot && WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
                Log("STARTUP_RECOVERY_SKIPPED: Proxy ownership or a listening process could not be safely resolved. Check Windows proxy settings.");
        }
        catch (Exception ex) { Log("STARTUP_RECOVERY_FAILED: " + ex.Message); }
    }

    private bool IsBluetoothSelected => _connectionType.SelectedIndex == 1;

    private Task ConnectSelectedAsync() =>
        IsBluetoothSelected ? StartBluetoothBridgeAsync() : StartUsbAsync();

    private TabPage BuildConnectionTab()
    {
        var page = new TabPage("Connect");
        var root = Panel();
        root.Controls.Add(Header("Connect your phone"));
        root.Controls.Add(_connectionState);
        root.Controls.Add(new Label { Text = "The bridge provides a Windows proxy for apps that support it.", Width = 780, Height = 24 });
        _connectionType.Items.AddRange(["USB", "Bluetooth"]);
        root.Controls.Add(_connectionType);

        var usb = ConnectionOptions();
        usb.Controls.Add(new Label { Text = "Start USB Bridge on Android and select the device here.", Width = 750, Height = 24 });
        _usbDevice.AccessibleName = "USB device";
        usb.Controls.Add(_usbDevice);
        usb.Controls.Add(Button("Refresh USB devices", RefreshUsbDevicesAsync, width: 180));
        usb.Controls.Add(Button("USB setup help", ShowUsbSetupAsync, width: 150));
        root.Controls.Add(usb);

        var bluetooth = ConnectionOptions();
        bluetooth.Controls.Add(new Label { Text = "Pair the phone in Windows and start Bluetooth Bridge on Android.", Width = 750, Height = 24 });
        _bluetoothDevice.AccessibleName = "Paired Bluetooth device";
        bluetooth.Controls.Add(_bluetoothDevice);
        bluetooth.Controls.Add(Button("Refresh paired devices", RefreshBluetoothDevicesAsync, width: 180));
        bluetooth.Controls.Add(Button("Pair with phone", PairBluetoothAsync, width: 180));
        bluetooth.Controls.Add(new Label { Text = "On Android, tap Pair with PC and approve this computer. Manual payload is optional.", Width = 750, Height = 24 });
        bluetooth.Controls.Add(_bluetoothPairingPayload);
        bluetooth.Controls.Add(_autoReconnectBluetooth);
        root.Controls.Add(bluetooth);

        _connectionType.SelectedIndexChanged += (_, _) =>
        {
            usb.Visible = !IsBluetoothSelected;
            bluetooth.Visible = IsBluetoothSelected;
        };
        _connectionType.SelectedIndex = 0;
        var actions = new FlowLayoutPanel { Width = 760, Height = 44, WrapContents = false };
        actions.Controls.Add(Button("Connect", ConnectSelectedAsync, width: 112));
        actions.Controls.Add(Button("Disconnect", RestoreProxyAsync, width: 112));
        actions.Controls.Add(Button("Test internet through bridge", TestBridgeInternetAsync, width: 220));
        root.Controls.Add(actions);
        page.Controls.Add(root);
        return page;
    }

    private static FlowLayoutPanel ConnectionOptions() => new()
    {
        Width = 800, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown, WrapContents = false
    };

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage("Advanced");
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildNetworkOptionsTab());
        tabs.TabPages.Add(BuildPairingTab());
        tabs.TabPages.Add(BuildTrafficTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        tabs.TabPages.Add(BuildLogsTab());
        page.Controls.Add(tabs);
        return page;
    }

    private TabPage BuildNetworkOptionsTab()
    {
        var page = new TabPage("Network and Recovery");
        var root = Panel();
        root.Controls.Add(_autoConnectUsb);
        root.Controls.Add(Button("Retry USB forward cleanup", RetryUsbCleanupAsync, width: 230));
        root.Controls.Add(Button("Open Windows Bluetooth settings", OpenBluetoothPanSettingsAsync, width: 255));
        root.Controls.Add(Button("Check native Bluetooth PAN", StartBluetoothPanAsync, width: 220));
        root.Controls.Add(WifiCard());
        root.Controls.Add(ModeCard("Wi-Fi / Hotspot Assist", "Checks likely phone hotspot/Wi-Fi gateways and gives setup guidance.", WifiAssistAsync));
        root.Controls.Add(ModeCard("Native Tether Assist", "Checks for existing native USB, Wi-Fi hotspot, or Bluetooth tethering routes.", NativeAssistAsync));
        root.Controls.Add(new Label { Text = "Bandwidth limit in KiB/s per direction (0 = unlimited; applies on next connection):", Width = 750, Height = 24 });
        root.Controls.Add(_bandwidthLimit);
        root.Controls.Add(ModeCard("Disable / Restore Proxy", "Restores the user's saved proxy settings.", RestoreProxyAsync));
        root.Controls.Add(new Label { Text = "Optional DNS resolver IP (requests use this provider):", Width = 650, Height = 24 });
        root.Controls.Add(_dnsUpstream);
        root.Controls.Add(new Label { Text = "Local DNS port (53 is required for Windows system DNS):", Width = 650, Height = 24 });
        root.Controls.Add(_dnsPort);
        root.Controls.Add(_dnsState);
        root.Controls.Add(Button("Start local DNS", StartDnsAsync, width: 180));
        root.Controls.Add(Button("Stop local DNS", StopDnsAsync, width: 180));
        root.Controls.Add(ModeCard("View Traffic Status", "Refreshes live local relay counters and Android /status if reachable.", ShowTrafficStatusAsync));
        root.Controls.Add(new Label
        {
            Text = "Full routing, automatic Windows DNS and Wi-Fi Share are available for testing in the separate Tunnel Lab.",
            Width = 800, Height = 40
        });
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildTrafficTab()
    {
        var page = new TabPage("Traffic");
        var root = Panel();
        root.Controls.Add(Header("Live traffic / status"));
        root.Controls.Add(new Label { Text = "Auto-refresh runs every 2 seconds while the app is open. Counters combine local relay stats with Android /status when a phone session is reachable.", Width = 800, Height = 48 });
        root.Controls.Add(_localStats);
        root.Controls.Add(_socksStats);
        root.Controls.Add(_phoneStats);
        root.Controls.Add(Button("Refresh now", async () => await ShowTrafficStatusAsync()));
        root.Controls.Add(Button("Run Diagnostics", async () => await RunDiagnosticsAsync()));
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildPairingTab()
    {
        var page = new TabPage("Pairing");
        var root = Panel();
        root.Controls.Add(Header("QR/manual pairing"));
        root.Controls.Add(new Label { Text = "Paste the current Android pairing URI, or enter the phone address, token, and certificate fingerprint below.", Width = 800, Height = 48 });
        root.Controls.Add(_pairingPayload);
        root.Controls.Add(Button("Pair from pasted URI", ApplyPairingPayloadAsync));
        root.Controls.Add(Button("Inject from USB phone", InjectPairingFromUsbAsync, width: 180));
        root.Controls.Add(new Label { Text = "Phone IP:", Width = 70 });
        root.Controls.Add(_phoneIp);
        root.Controls.Add(new Label { Text = "Port:", Width = 70 });
        root.Controls.Add(_phonePort);
        root.Controls.Add(new Label { Text = "Token:", Width = 70 });
        root.Controls.Add(_token);
        root.Controls.Add(new Label { Text = "Certificate SHA-256:", Width = 150 });
        root.Controls.Add(_fingerprint);
        root.Controls.Add(Button("Start Wi-Fi from pairing", StartWifiAsync));
        root.Controls.Add(_modeGuide);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildDiagnosticsTab()
    {
        var page = new TabPage("Diagnostics");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        top.Controls.Add(Button("Run Diagnostics", RunDiagnosticsAsync));
        top.Controls.Add(Button("Export Diagnostics", ExportDiagnosticsAsync));
        top.Controls.Add(Button("Export Support Bundle", ExportSupportBundleAsync, width: 180));
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_diagnosticsBox);
        panel.Controls.Add(top);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildLogsTab()
    {
        var page = new TabPage("Logs");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        top.Controls.Add(Button("Export Logs", ExportLogsAsync));
        top.Controls.Add(Button("Export Support Bundle", ExportSupportBundleAsync, width: 180));
        top.Controls.Add(Button("Clear View", () => { _log.Clear(); _logLines.Clear(); return Task.CompletedTask; }));
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_log);
        panel.Controls.Add(top);
        page.Controls.Add(panel);
        return page;
    }

    private FlowLayoutPanel Panel() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16),
        AutoScroll = true
    };

    private Control Header(string text) => new Label { Text = text, Font = new Font(Font, FontStyle.Bold), Width = 800, Height = 28 };

    private Control WifiCard()
    {
        var panel = Card("Wi-Fi Bridge", "Connects to the phone by IP and pairing token. Windows still points only at the local relay.");
        panel.Controls.Add(new Label { Text = "Set Phone IP and Token in the Pairing tab, then start here or from Pairing.", Width = 260, Height = 42 });
        panel.Controls.Add(Button("Start Wi-Fi", StartWifiAsync, width: 110));
        return panel;
    }

    private Control ModeCard(string title, string body, Func<Task> action)
    {
        var panel = Card(title, body);
        panel.Controls.Add(Button(title.StartsWith("Disable") ? "Restore" : "Start / Check", action, width: 120));
        return panel;
    }

    private FlowLayoutPanel Card(string title, string body)
    {
        var p = new FlowLayoutPanel { Width = 820, Height = 84, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        p.Controls.Add(new Label { Text = title + Environment.NewLine + body, Width = 400, Height = 62 });
        return p;
    }

    private Button Button(string text, Func<Task> action, int width = 150)
    {
        var btn = new Button { Text = text, Width = width, Height = 30 };
        btn.Click += async (_, _) =>
        {
            try { await _actionGate.RunAsync(action); }
            catch (Exception ex) { Log($"ACTION_FAILED: {text}: {ex.Message}"); }
        };
        return btn;
    }

    private string? SelectedUsbSerial => (_usbDevice.SelectedItem as AdbDevice)?.Serial;

    private async Task RefreshUsbDevicesAsync()
    {
        var previous = SelectedUsbSerial;
        var (result, devices) = await new AdbService().ListDevicesAsync();
        if (IsDisposed) return;
        Log(result.ToDisplayString());
        if (!result.Ok) return;
        if (!_usbHotplug.IsInitialized) _usbHotplug.Observe(devices);
        _usbDevice.BeginUpdate();
        try
        {
            _usbDevice.Items.Clear();
            _usbDevice.Items.Add("Automatic (one authorized device)");
            foreach (var device in devices) _usbDevice.Items.Add(device);
            var selected = devices.FirstOrDefault(d => d.Serial == previous);
            if (previous is not null && selected is null)
            {
                selected = new AdbDevice(previous, "offline", "Previously selected device");
                _usbDevice.Items.Add(selected);
            }
            _usbDevice.SelectedItem = selected is null ? _usbDevice.Items[0] : selected;
        }
        finally { _usbDevice.EndUpdate(); }
    }

    private async Task ScanForUsbHotplugAsync()
    {
        if (_usbScanInFlight || IsDisposed) return;
        _usbScanInFlight = true;
        try
        {
            var (result, devices) = await new AdbService().ListDevicesAsync();
            if (IsDisposed || !result.Ok) return;
            var added = _usbHotplug.Observe(devices);
            if (!_autoConnectUsb.Checked || added.Length == 0 || _session is not null) return;
            var selected = SelectedUsbSerial;
            var ready = devices.Where(device => device.State == "device").ToArray();
            if (selected is not null ? !added.Contains(selected, StringComparer.Ordinal) : ready.Length != 1 || !added.Contains(ready[0].Serial, StringComparer.Ordinal)) return;
            var candidate = selected ?? ready[0].Serial;
            await _actionGate.RunAsync(async () =>
            {
                if (!_autoConnectUsb.Checked || _session is not null || _exitRequested || _profiles.HasActiveSnapshot || SelectedUsbSerial != selected ||
                    WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort)) return;
                var (freshResult, freshDevices) = await new AdbService().ListDevicesAsync();
                if (!freshResult.Ok || IsDisposed || !_autoConnectUsb.Checked || _session is not null || _exitRequested ||
                    _profiles.HasActiveSnapshot || SelectedUsbSerial != selected || WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort)) return;
                var freshReady = freshDevices.Where(device => device.State == "device").ToArray();
                if (!freshReady.Any(device => device.Serial == candidate) || (selected is null && freshReady.Length != 1)) return;
                bool Eligible() => !IsDisposed && _autoConnectUsb.Checked && !_exitRequested && !_profiles.HasActiveSnapshot &&
                    SelectedUsbSerial == selected && !WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort);
                await StartUsbAsync(Eligible, candidate);
            });
        }
        catch (Exception ex) { Log("USB_HOTPLUG_CHECK_FAILED: " + ex.Message); }
        finally { _usbScanInFlight = false; }
    }

    private Task StartUsbAsync() => StartUsbAsync(null, null);

    private async Task<DiagnosticResult> ReleaseOrRememberUsbForwardAsync(AdbService adb)
    {
        _pendingUsbCleanup.Add(adb);
        var result = await adb.ReleaseOwnedForwardAsync();
        if (result.Ok) _pendingUsbCleanup.Remove(adb);
        Log(result.ToDisplayString());
        return result;
    }

    private async Task RetryUsbCleanupAsync()
    {
        if (_pendingUsbCleanup.Count == 0)
        {
            Log("ADB_CLEANUP_IDLE: No USB cleanup is pending.");
            return;
        }
        foreach (var owner in _pendingUsbCleanup.ToArray())
            await ReleaseOrRememberUsbForwardAsync(owner);
    }

    private async Task ShowUsbSetupAsync()
    {
        var results = new List<DiagnosticResult> { await UsbDevicePresence.CheckAsync(), new NativeTetherInspector().CheckUsbRoute() };
        var adb = new AdbService(SelectedUsbSerial);
        var device = await adb.CheckAsync();
        results.Add(device);
        if (device.Ok)
        {
            var package = await adb.QueryPackageVersionAsync();
            results.Add(package);
            if (package.Ok)
            {
                var (forward, port) = await adb.AllocateForwardAsync(10889);
                if (!forward.Ok) results.Add(forward);
                else
                {
                    try { results.Add(await new TcpPhoneTransport("127.0.0.1", port, framedUsb: true).ProbeHealthAsync(null)); }
                    finally { results.Add(await ReleaseOrRememberUsbForwardAsync(adb)); }
                }
            }
        }

        foreach (var result in results) Log(result.ToDisplayString());
        var details = string.Join("\n\n", results.Select(result => result.Ok
            ? result.Message : result.Message + "\n" + result.Fix));
        MessageBox.Show(this,
            "Native USB: use Android's USB tethering switch. This path uses the Windows network adapter.\n\n" +
            "USB Bridge: enable USB debugging, authorize this PC and tap Start USB Bridge in Android. " +
            "Then use USB Bridge in this app for HTTP and SOCKS proxy access.\n\n" + details,
            "USB setup and checks", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task StartUsbAsync(Func<bool>? stillWanted, string? deviceSerial)
    {
        var adb = new AdbService(deviceSerial ?? SelectedUsbSerial);
        var diag = await adb.CheckAsync();
        Log(diag.ToDisplayString());
        if (!diag.Ok || (stillWanted is not null && !stillWanted()))
        {
            if (!diag.Ok && _session is null) _connectionState.Text = "USB device unavailable. Use USB setup help.";
            return;
        }
        if (_usbAdb is { } currentAdb && currentAdb.HasSameSelectedDevice(adb) &&
            _session is { } currentSession && _relay.IsRunning && _socksRelay.Port != 0)
        {
            var existingHealth = await currentSession.Transport.ProbeHealthAsync(currentSession.Token);
            if (existingHealth.Ok)
            {
                Log("USB_ALREADY_CONNECTED: The selected tablet is already connected.");
                return;
            }
        }
        var (forward, localPort) = await adb.AllocateForwardAsync(10889);
        Log(forward.ToDisplayString());
        if (!forward.Ok) return;

        var transferred = false;
        try
        {
            if (stillWanted is not null && !stillWanted()) return;
            var transport = LimitTransport(new TcpPhoneTransport("127.0.0.1", localPort, framedUsb: true));
            var health = await transport.ProbeHealthAsync(null);
            Log(health.ToDisplayString());
            if (!health.Ok || (stillWanted is not null && !stillWanted()))
            {
                if (!health.Ok && _session is null) _connectionState.Text = "The Android USB bridge did not respond. Use USB setup help.";
                return;
            }

            await PrepareForConnectionChangeAsync();
            if (stillWanted is not null && !stillWanted()) return;
            var session = new PhoneBridgeSession(transport, Token: null,
                ReconnectAsync: () => adb.RecoverUsbBridgeAsync(localPort, 10889, () => transport.ProbeHealthAsync(null)));
            _usbAdb = adb;
            _session = session;
            transferred = true;
            await EnableLocalRelayAndProxyAsync(session, "USB Bridge", stillWanted);
        }
        finally
        {
            if (!transferred) await ReleaseOrRememberUsbForwardAsync(adb);
        }
    }

    private async Task StartWifiAsync()
    {
        if (string.IsNullOrWhiteSpace(_phoneIp.Text))
        {
            Log("PHONE_IP_REQUIRED: Enter the host shown in the Android pairing URI or QR details.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_token.Text))
        {
            Log("TOKEN_REQUIRED: Enter the token shown on the Android app.");
            return;
        }
        var fingerprint = _fingerprint.Text.Trim();
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            Log("CERTIFICATE_FINGERPRINT_REQUIRED: Pair with the current Android app or enter its 64-character fingerprint.");
            return;
        }
        if (!int.TryParse(_phonePort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Log("PHONE_PORT_INVALID: Enter the port shown in the Android pairing details.");
            return;
        }
        var session = new PhoneBridgeSession(LimitTransport(new TcpPhoneTransport(_phoneIp.Text.Trim(), port, fingerprint)), _token.Text.Trim());
        await SwitchToWirelessSessionAsync(session, "Wi-Fi Bridge");
    }


    private Task WifiAssistAsync()
    {
        foreach (var r in new WifiHotspotInspector().Check()) Log(r.ToDisplayString());
        Log("WIFI_GUIDANCE: For Wi-Fi Direct, create the group on Android, join its network from Windows and load the Android pairing URI.");
        return Task.CompletedTask;
    }

    private async Task RefreshBluetoothDevicesAsync()
    {
        var previous = (_bluetoothDevice.SelectedItem as BluetoothPeer)?.Id;
        var peers = await BluetoothRfcommTransport.ListPairedAsync();
        _bluetoothDevice.Items.Clear();
        foreach (var peer in peers) _bluetoothDevice.Items.Add(peer);
        var selected = peers.FirstOrDefault(peer => peer.Id == previous);
        if (selected is not null) _bluetoothDevice.SelectedItem = selected;
        else if (_bluetoothDevice.Items.Count > 0) _bluetoothDevice.SelectedIndex = 0;
        Log($"BLUETOOTH_PAIRED: Found {peers.Count} paired Bluetooth devices.");
    }

    private async Task PairBluetoothAsync()
    {
        if (_bluetoothDevice.SelectedItem is not BluetoothPeer peer)
        {
            _connectionState.Text = "Choose a paired Bluetooth device first.";
            return;
        }
        _connectionState.Text = "Waiting for approval on the phone. Open TetherBridge and tap Pair with PC first.";
        try
        {
            _bluetoothPairings[peer.Id] = await BluetoothRfcommTransport.RequestPairingAsync(peer.Id);
            _bluetoothPairingPayload.Clear();
            _connectionState.Text = "Phone approved. Start Bluetooth Bridge on Android, then click Connect.";
        }
        catch (Exception ex)
        {
            _connectionState.Text = ex is EndOfStreamException or OperationCanceledException
                ? "Bluetooth pairing was declined, disconnected, or timed out."
                : "Bluetooth pairing failed. Open the Android app and tap Pair with PC, then try again.";
            Log($"BLUETOOTH_PAIRING_FAILED: {ex.GetType().Name}");
        }
    }

    private async Task StartBluetoothBridgeAsync()
    {
        PairingPayload? manualPairing = null;
        if (!string.IsNullOrWhiteSpace(_bluetoothPairingPayload.Text))
        {
            if (!PairingPayloadParser.TryParse(_bluetoothPairingPayload.Text, out var parsed, out var error))
            {
                _connectionState.Text = "Bluetooth pairing details could not be loaded.";
                Log(error);
                return;
            }
            manualPairing = parsed;
        }
        if (_bluetoothDevice.SelectedItem is not BluetoothPeer peer)
        {
            _connectionState.Text = "Choose a paired Bluetooth device.";
            Log("BLUETOOTH_DEVICE_REQUIRED: Refresh and select a paired Android device.");
            return;
        }
        var pairing = manualPairing ?? _bluetoothPairings.GetValueOrDefault(peer.Id);
        var fingerprint = pairing?.Fingerprint ?? _fingerprint.Text.Trim();
        var pairingToken = pairing?.Token ?? _token.Text.Trim();
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(pairingToken))
        {
            _connectionState.Text = "Tap Pair with PC on Android, then click Pair with phone here.";
            Log("BLUETOOTH_PAIRING_REQUIRED: Load the Android pairing token and certificate fingerprint first.");
            return;
        }
        var transport = new BluetoothRfcommTransport(peer.Id, fingerprint);
        var session = new PhoneBridgeSession(LimitTransport(transport), pairingToken,
            BluetoothHealthAsync: _autoReconnectBluetooth.Checked ? token => transport.ProbeHealthAsync(pairingToken, token) : null);
        await SwitchToWirelessSessionAsync(session, "Bluetooth Bridge");
    }

    private async Task SwitchToWirelessSessionAsync(PhoneBridgeSession candidate, string modeName)
    {
        var health = await ConnectionTransition.PrepareIfReadyAsync(
            () => candidate.Transport.ProbeHealthAsync(candidate.Token), PrepareForConnectionChangeAsync);
        Log(health.ToDisplayString());
        if (!health.Ok)
        {
            if (_session is null) _connectionState.Text = $"{modeName} did not respond. See Advanced > Logs.";
            return;
        }

        _session = candidate;
        await EnableLocalRelayAndProxyAsync(candidate, modeName);
    }

    private Task OpenBluetoothPanSettingsAsync()
    {
        Log("BLUETOOTH_PAN_SETUP: Enable Bluetooth tethering on the phone. In Windows Devices, expand the phone, then choose Personal Area Network > Join > Access Point.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private Task StartBluetoothPanAsync()
    {
        Log(new BluetoothPanInspector().CheckPanAvailability().ToDisplayString());
        return Task.CompletedTask;
    }

    private Task NativeAssistAsync()
    {
        foreach (var r in new NativeTetherInspector().Check()) Log(r.ToDisplayString());
        foreach (var r in new WifiHotspotInspector().Check()) Log(r.ToDisplayString());
        return Task.CompletedTask;
    }

    private async Task ApplyPairingPayloadAsync()
    {
        if (!PairingPayloadParser.TryParse(_pairingPayload.Text, out var payload, out var error))
        {
            Log("PAIRING_PARSE_ERROR: " + error);
            return;
        }
        _phoneIp.Text = payload.Host;
        _phonePort.Text = payload.Port.ToString();
        _token.Text = payload.Token;
        _fingerprint.Text = payload.Fingerprint;
        Log($"PAIRING_APPLIED: mode={payload.Mode}, host={payload.Host}, port={payload.Port}, token-length={payload.Token.Length}");
        UpdateModeGuide();
        await PollStatusAsync(silent: true);
    }

    private async Task InjectPairingFromUsbAsync()
    {
        var adb = new AdbService(SelectedUsbSerial);
        var adbCheck = await adb.CheckAsync();
        Log(adbCheck.ToDisplayString());
        if (!adbCheck.Ok) return;

        var (result, payload) = await adb.ReadUsbPairingPayloadAsync();
        Log(result.ToDisplayString());
        if (!result.Ok) return;

        _pairingPayload.Text = payload;
        await ApplyPairingPayloadAsync();
    }

    private IPhoneTransport LimitTransport(IPhoneTransport transport)
    {
        var limit = (long)_bandwidthLimit.Value * 1024;
        return limit == 0 ? transport : new BandwidthLimitedTransport(transport, limit);
    }

    private async Task EnableLocalRelayAndProxyAsync(PhoneBridgeSession session, string modeName, Func<bool>? stillWanted = null)
    {
        var applyingProxy = false;
        await ConnectionTransition.ActivateAsync(async () =>
        {
            var phoneStatus = await session.Transport.ProbeStatusAsync(session.Token);
            var udpEnabled = phoneStatus.Ok && PhoneCapabilities.SupportsUdp(phoneStatus.Message);
            Log(udpEnabled
                ? "PHONE_UDP_AVAILABLE: The Android bridge supports UDP forwarding."
                : "PHONE_UDP_UNAVAILABLE: TCP remains available; update the Android app for UDP forwarding.");

            if (stillWanted is not null)
            {
                if (!stillWanted()) { await StopExistingAsync(); return; }
                var ready = _usbAdb is not null && (await _usbAdb.CheckAsync()).Ok;
                if (!ready || !stillWanted()) { await StopExistingAsync(); return; }
            }
            if (WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort) &&
                !WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", LocalHttpProxyPort))
            {
                Log("PROXY_CONFLICT: Windows proxy settings were modified while referencing TetherBridge. Resolve them in Windows before connecting again.");
                await StopExistingAsync();
                _connectionState.Text = "State: proxy settings conflict; resolve in Windows";
                return;
            }

            _relay.Start(session, localPort: LocalHttpProxyPort, Log);
            Log($"LOCAL_PROXY_OK: HTTP relay listening on 127.0.0.1:{LocalHttpProxyPort}");
            _socksRelay.Start(session, localPort: LocalSocks5Port, Log, allowUdp: udpEnabled);
            _udpAvailable = udpEnabled;
            Log($"LOCAL_SOCKS5_OK: {(udpEnabled ? "TCP/UDP" : "TCP-only")} SOCKS5 relay listening on 127.0.0.1:{LocalSocks5Port}");

            if (!_profiles.SaveCurrentBeforeEnableIfNeeded("127.0.0.1", LocalHttpProxyPort, out var snapshotMessage))
                throw new IOException(snapshotMessage);
            Log(snapshotMessage);
            applyingProxy = true;
            WindowsProxySettings.EnableLocalProxy("127.0.0.1", LocalHttpProxyPort);
            Log("WINDOWS_PROXY_ENABLED: Windows current-user proxy now points to local relay.");
            _connectionState.Text = $"{modeName} connected. Proxy ready for supported Windows apps.";
            StartSessionMonitorAsync(session);
            await PollStatusAsync(silent: true);
        }, async () =>
        {
            if (applyingProxy && WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", LocalHttpProxyPort))
                await RestoreProxyAsync();
            else
                await StopExistingAsync();

            if (_relay.IsRunning || _session is not null)
                throw new IOException("The connection still needs cleanup. Review the proxy settings.");
            _connectionState.Text = "State: connection setup failed";
        });
    }

    private async Task TestBridgeInternetAsync()
    {
        if (_session is null || !_relay.IsRunning || _socksRelay.Port == 0)
        {
            _connectionState.Text = "Connect the USB or Bluetooth bridge first.";
            return;
        }
        _connectionState.Text = "Checking internet access through the bridge proxy...";
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{LocalHttpProxyPort}"),
                UseProxy = true, AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var result = await client.GetAsync("https://example.com/", HttpCompletionOption.ResponseHeadersRead);
            result.EnsureSuccessStatusCode();
            _connectionState.Text = "Proxy internet verified. Apps that bypass the proxy still need a network connection.";
        }
        catch (Exception error)
        {
            _connectionState.Text = "The local proxy is ready, but the internet test failed. See Advanced > Logs.";
            Log("PROXY_INTERNET_CHECK_FAILED: " + error.Message);
        }
    }

    private async Task RestoreProxyAsync()
    {
        if (!_relay.IsRunning && WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(endpoint => endpoint.Port == LocalHttpProxyPort))
            {
                Log("PROXY_RESTORE_SKIPPED: Another process is listening on the proxy port; current settings were preserved.");
                return;
            }
            var saved = _profiles.ReadActiveSnapshot();
            if (saved is null) { Log("PROXY_RESTORE_SKIPPED: No valid active recovery record is available."); return; }
            var current = WindowsProxySettings.ReadCurrent();
            var choice = MessageBox.Show(
                $"A previous session may have left a proxy configured. Another program could also have chosen these settings.\n\nCurrent: {FormatProxyForReview(current)}\n\nSaved: {FormatProxyForReview(saved)}\n\nRestore the saved settings?",
                "Review proxy recovery", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }
        var restored = _profiles.TryRestoreLatest(out var message);
        Log(message);
        if (WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
        {
            Log("PROXY_CONFLICT: Windows still references the local relay in modified settings. Relay remains running; resolve the proxy settings in Windows before disconnecting.");
            _connectionState.Text = "State: proxy settings conflict; relay remains running";
            return;
        }
        await StopExistingAsync();
        _connectionState.Text = restored ? "Disconnected. Previous proxy settings restored." : "Disconnected.";
        await PollStatusAsync(silent: true);
    }

    private static string FormatProxyForReview(ProxyState state) =>
        $"Enabled: {state.ProxyEnable}; Server: {state.ProxyServer ?? "(none)"}; Bypass: {state.ProxyOverride ?? "(none)"}; PAC: {state.AutoConfigUrl ?? "(none)"}; Auto-detect: {state.AutoDetect?.ToString() ?? "(unchanged)"}";

    private async Task RunDiagnosticsAsync()
    {
        var diag = new DiagnosticsService(SelectedUsbSerial);
        var results = await diag.RunAllAsync();
        var text = string.Join(Environment.NewLine, results.Select(r => r.ToDisplayString()));
        _diagnosticsBox.Text = text;
        foreach (var result in results) Log(result.ToDisplayString());
    }

    private async Task ShowTrafficStatusAsync()
    {
        await PollStatusAsync(silent: false);
    }

    private Task RefreshTrafficStatusAsync(bool silent) => PollStatusAsync(silent);

    private async Task PollStatusAsync(bool silent)
    {
        if (_statusPollInFlight || IsDisposed) return;
        _statusPollInFlight = true;
        try
        {
            var relaySnapshot = _relay.GetSnapshot();
            _localStats.Text = "Local relay: " + relaySnapshot.ToDisplayString();
            _socksStats.Text = "SOCKS5 relay: " + _socksRelay.GetSnapshot().ToDisplayString();
            if (!silent) Log("LOCAL_RELAY_STATS: " + relaySnapshot.ToDisplayString());

            if (_session is null)
            {
                _phoneStats.Text = "Phone status: no active bridge session";
                if (!silent) Log("PHONE_STATUS_SKIPPED: No active bridge session.");
                return;
            }

            var status = await _session.Transport.ProbeStatusAsync(_session.Token);
            if (!status.Ok)
            {
                _phoneStats.Text = "Phone status: " + status.ToDisplayString();
                if (!silent) Log(status.ToDisplayString());
                return;
            }

            if (PhoneStatusSnapshot.TryParse(status.Message, out var snapshot, out var parseError))
            {
                _phoneStats.Text = "Phone status: " + snapshot.ToDisplayString();
                if (!silent) Log("PHONE_STATUS_OK: " + snapshot.ToDisplayString());
            }
            else
            {
                _phoneStats.Text = "Phone status raw: " + status.Message;
                if (!silent) Log("PHONE_STATUS_PARSE_WARNING: " + parseError + " | Raw: " + status.Message);
            }
        }
        catch (Exception ex)
        {
            if (!silent) Log("STATUS_REFRESH_ERROR: " + ex.Message);
        }
        finally
        {
            _statusPollInFlight = false;
        }
    }

    private Task StartDnsAsync()
    {
        if (!_udpAvailable || _session is null) { Log("DNS_UNAVAILABLE: Start a UDP-capable bridge first."); return Task.CompletedTask; }
        if (!System.Net.IPAddress.TryParse(_dnsUpstream.Text.Trim(), out var upstream)) { Log("DNS_BAD_UPSTREAM: Enter a valid DNS server IP address."); return Task.CompletedTask; }
        if (!int.TryParse(_dnsPort.Text.Trim(), out var port) || port is < 1 or > 65535) { Log("DNS_BAD_PORT: Enter a valid local DNS port."); return Task.CompletedTask; }
        _dnsProxy.Stop();
        var resolver = new SocksDnsResolver(LocalSocks5Port, upstream);
        _dnsProxy.Start(resolver.ResolveAsync, port, resolver.ResolveTcpAsync);
        _dnsState.Text = $"DNS proxy: 127.0.0.1:{_dnsProxy.Port}" + (_dnsProxy.Ipv6Enabled ? $", [::1]:{_dnsProxy.Port}" : "") + $"; upstream {upstream}; Windows DNS unchanged";
        Log($"DNS_PROXY_STARTED: Listening on 127.0.0.1:{_dnsProxy.Port}.");
        return Task.CompletedTask;
    }

    private Task StopDnsAsync()
    {
        _dnsProxy.Stop();
        _dnsState.Text = "DNS proxy stopped";
        return Task.CompletedTask;
    }

    private async Task StopExistingAsync()
    {
        if (_relay.IsRunning && WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
            throw new InvalidOperationException("Windows still references the TetherBridge relay; it will remain running until proxy settings are resolved.");
        await StopDnsAsync();
        _udpAvailable = false;
        _sessionMonitorCts?.Cancel();
        _sessionMonitorCts?.Dispose();
        _sessionMonitorCts = null;
        _relay.Stop();
        _socksRelay.Stop();
        _session = null;
        var adb = _usbAdb;
        _usbAdb = null;
        if (adb is not null) await ReleaseOrRememberUsbForwardAsync(adb);
    }

    private async Task PrepareForConnectionChangeAsync()
    {
        if (WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort) &&
            !WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", LocalHttpProxyPort))
            throw new InvalidOperationException("Windows proxy settings still reference TetherBridge but were modified. Resolve them before changing connections; the relay remains running.");
        await ConnectionTransition.PrepareAsync(
            () => WindowsProxySettings.PointsAtLocalProxy("127.0.0.1", LocalHttpProxyPort),
            RestoreProxyAsync,
            StopExistingAsync);
        if (WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort))
            throw new InvalidOperationException("Windows still references the TetherBridge relay. Resolve proxy settings before changing connections.");
    }

    private void StartSessionMonitorAsync(PhoneBridgeSession session)
    {
        _sessionMonitorCts?.Cancel();
        _sessionMonitorCts?.Dispose();
        _sessionMonitorCts = new CancellationTokenSource();
        var ct = _sessionMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            string lastFailure = "";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    var health = session.BluetoothHealthAsync is { } probeBluetooth
                        ? await probeBluetooth(ct)
                        : await session.Transport.ProbeHealthAsync(session.Token);
                    if (health.Ok)
                    {
                        if (consecutiveFailures > 0) Log("PHONE_BRIDGE_HEALTH_RECOVERED: Health check passed after transient failures.");
                        consecutiveFailures = 0;
                        lastFailure = "";
                        continue;
                    }

                    lastFailure = health.ToDisplayString();
                    if (session.BluetoothHealthAsync is { } bluetoothRecovery)
                    {
                        Log("BLUETOOTH_RECONNECTING: Retrying the selected phone. Disconnect cancels these retries.");
                        var recovered = await BluetoothConnectionRecovery.RetryAsync(bluetoothRecovery, ct);
                        if (recovered.Ok)
                        {
                            Log("BLUETOOTH_RECONNECTED: New connections can use the bridge again.");
                            consecutiveFailures = 0;
                            continue;
                        }
                        BeginRestoreAfterSessionLoss(session, "BLUETOOTH_RECONNECT_FAILED: " + recovered.ToDisplayString());
                        return;
                    }
                    DiagnosticResult? reconnect = null;
                    await _actionGate.RunAsync(async () =>
                    {
                        if (!ct.IsCancellationRequested && ReferenceEquals(_session, session)) reconnect = await session.TryReconnectAsync();
                    });
                    if (reconnect is not null)
                    {
                        Log("USB_RECONNECT_ATTEMPT: " + reconnect.ToDisplayString());
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        var retryHealth = await session.Transport.ProbeHealthAsync(session.Token);
                        if (retryHealth.Ok)
                        {
                            Log("USB_RECONNECTED: Phone bridge recovered after re-establishing adb forward.");
                            consecutiveFailures = 0;
                            lastFailure = "";
                            continue;
                        }

                        lastFailure = retryHealth.ToDisplayString();
                    }

                    consecutiveFailures++;
                    Log($"PHONE_BRIDGE_HEALTH_RETRY_FAILED: {consecutiveFailures}/{SessionMonitorFailureThreshold}: {lastFailure}");
                    if (consecutiveFailures < SessionMonitorFailureThreshold) continue;

                    BeginRestoreAfterSessionLoss(session, "PHONE_BRIDGE_LOST: " + lastFailure);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    lastFailure = $"{ex.GetType().Name}: {ex.Message}";
                    Log($"PHONE_BRIDGE_MONITOR_ERROR: {consecutiveFailures}/{SessionMonitorFailureThreshold}: {lastFailure}");
                    if (consecutiveFailures < SessionMonitorFailureThreshold) continue;

                    BeginRestoreAfterSessionLoss(session, "PHONE_BRIDGE_MONITOR_ERROR: " + lastFailure);
                    return;
                }
            }
        }, ct);
    }

    private void BeginRestoreAfterSessionLoss(PhoneBridgeSession session, string reason)
    {
        if (_restoringAfterSessionLoss || IsDisposed) return;
        _restoringAfterSessionLoss = true;

        void StartRestore()
        {
            _ = RestoreAfterSessionLossAsync(session, reason);
        }

        if (IsHandleCreated) BeginInvoke((Action)StartRestore);
        else _ = RestoreAfterSessionLossAsync(session, reason);
    }

    private async Task RestoreAfterSessionLossAsync(PhoneBridgeSession session, string reason)
    {
        try
        {
            await _actionGate.RunAsync(async () =>
            {
                if (!ReferenceEquals(_session, session)) return;
                Log(reason);
                Log("SESSION_LOST_RESTORE: Stopping relay and restoring saved Windows proxy state.");
                await RestoreProxyAsync();
            });
        }
        catch (InvalidOperationException) when (_exitRequested || IsDisposed) { }
        catch (Exception ex) { ReportTrayFailure("Connection recovery failed", ex); }
        finally
        {
            _restoringAfterSessionLoss = false;
        }
    }

    private async Task ExportLogsAsync()
    {
        await SaveTextAsync("tetherbridge-logs.txt", string.Join(Environment.NewLine, _logLines.Snapshot()));
    }

    private async Task ExportDiagnosticsAsync()
    {
        var diag = new DiagnosticsService(SelectedUsbSerial);
        var results = await diag.RunAllAsync();
        var report = await SupportBundleExporter.BuildAsync(
            _logLines.Snapshot(300),
            _relay.GetSnapshot(),
            _socksRelay.GetSnapshot(),
            _session,
            results);
        await SaveTextAsync("tetherbridge-diagnostics-redacted.txt", report);
    }


    private async Task ExportSupportBundleAsync()
    {
        var diagnostics = await new DiagnosticsService(SelectedUsbSerial).RunAllAsync();
        var adbEvidence = await new AdbService(SelectedUsbSerial).CollectEvidenceAsync();
        var content = await SupportBundleExporter.BuildAsync(_logLines.Snapshot(), _relay.GetSnapshot(), _socksRelay.GetSnapshot(), _session, diagnostics, adbEvidence);
        await SaveTextAsync("tetherbridge-support-bundle.txt", content);
    }

    private async Task SaveTextAsync(string defaultFileName, string content)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = defaultFileName,
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllTextAsync(dialog.FileName, content, Encoding.UTF8);
        Log("EXPORT_OK: " + dialog.FileName);
    }

    private void UpdateModeGuide()
    {
        _modeGuide.Text = "Mode guide: USB is most reliable. Wi-Fi Bridge needs the phone IP shown by Android and the pairing token/QR URI. Bluetooth/PAN Assist checks whether Windows has a PAN route; if it does, use the PAN phone IP in Wi-Fi Bridge. The local HTTP proxy is 127.0.0.1:10809 and the TCP/UDP SOCKS5 relay is 127.0.0.1:10810 for future tun2socks tests. Full-computer routing remains unavailable in this build. Wi-Fi Direct and Bluetooth Bridge require compatible updated Android and Windows apps.";
    }

    private void Log(string line)
    {
        if (IsDisposed) return;
        var formatted = $"[{DateTime.Now:T}] {RecentLogBuffer.Redact(line, _session?.Token)}";
        _logLines.Add(formatted);

        void Append()
        {
            _log.AppendText(formatted + Environment.NewLine);
        }

        if (InvokeRequired && IsHandleCreated)
        {
            BeginInvoke((Action)Append);
            return;
        }
        Append();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        var closeAction = _closeLifecycle.RequestClose(e.CloseReason == CloseReason.UserClosing && !_exitRequested);
        if (closeAction == CloseAction.AllowClose) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        if (closeAction == CloseAction.HideToTray) { Hide(); return; }
        if (closeAction == CloseAction.WaitForCleanup) return;
        _exitRequested = true;
        try
        {
            var mayClose = await _actionGate.CloseAsync(async () =>
            {
                if (_usbAdb is not null || _relay.IsRunning)
                {
                    Log("APP_CLOSING_RESTORE: Restoring proxy before exit.");
                    await RestoreProxyAsync();
                }
                await RetryUsbCleanupAsync();
                return !_relay.IsRunning || !WindowsProxySettings.ReferencesLocalProxy("127.0.0.1", LocalHttpProxyPort);
            });
            _closeLifecycle.Complete(mayClose);
            if (mayClose) Close();
            else { _exitRequested = false; Log("APP_CLOSE_BLOCKED: Resolve Windows proxy settings before closing; the relay remains available."); }
        }
        catch (Exception ex)
        {
            _closeLifecycle.Complete(false); _exitRequested = false;
            Log("APP_CLOSING_RESTORE_ERROR: " + ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _usbScanTimer.Stop();
            _usbScanTimer.Dispose();
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _sessionMonitorCts?.Cancel();
            _sessionMonitorCts?.Dispose();
            _dnsProxy.Stop();
            _relay.Stop();
            _socksRelay.Stop();
        }
        base.Dispose(disposing);
    }
}
