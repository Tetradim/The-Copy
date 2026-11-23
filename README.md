**The Copy** contains the TetherBridge Windows and Android source, the Windows Tunnel Lab, tests, packaging scripts, and independent PdaNet comparisons.

- [Downloads](https://github.com/Tetradim/The-Copy/releases) for Windows and Android test builds.
- [USB and Bluetooth review](docs/reviews/PdaNet_TetherBridge_USB_Bluetooth_Review.pdf)
- [Completion plan](docs/reviews/TetherBridge_PdaNet_Completion_Plan.pdf) and [editable Word version](docs/reviews/TetherBridge_PdaNet_Completion_Plan.docx)
- [Current progress](docs/parity-progress.md) and [PdaNet reference materials](docs/pdanet-reference.md)

Desktop proxy mode has passed isolated Samsung tests. Full-computer routing, automatic system DNS, and Wi-Fi Share are experimental Tunnel Lab features awaiting live validation. See the [tunnel lab instructions](docs/full-tunnel-design.md) for the required test setup.


> **Current test builds: Windows 0.6.12 and Android 0.6.8.** Desktop proxy mode supports framed USB, encrypted Wi-Fi and Bluetooth. Full routing, system DNS and Wi-Fi Share are experimental Tunnel Lab features awaiting live validation. See [test results](docs/parity-progress.md) and the [build and release guide](docs/release-maintenance.md). The older version notes below are historical.

# TetherBridge Classic — Clean-room v0.6.2

TetherBridge Classic is a clean-room Android + Windows companion design for phone-to-laptop connectivity.
It is intentionally *not* a clone of any existing product: no original package names, branding, icons,
strings, binary patches, signing certificates, license checks, or private protocols are used.

The design uses a local Windows relay instead of pointing Windows directly at the phone:

```text
Windows apps
  -> Windows system proxy: 127.0.0.1:10809
  -> TetherBridge Windows local relay
  -> USB/Wi-Fi/Bluetooth transport
  -> TetherBridge Android bridge service
  -> Android outbound network
```

This gives a paired-app workflow while keeping pairing tokens, cleanup, failover, and mode switching under
the Windows companion's control.

## Modes

| Mode | Primary goal | Transport | Windows system changes |
|---|---|---|---|
| USB Bridge | Most reliable app-based bridge | ADB forward to phone proxy | WinINet proxy to local relay |
| Wi-Fi Bridge | Cable-free bridge over an existing reachable network | TCP to phone IP + token | WinINet proxy to local relay |
| Bluetooth/PAN Assist | Works when Bluetooth tethering/PAN is available | Native PAN or TCP over PAN | Either none, proxy, or future full tunnel |
| Native Tether Assist | Use built-in USB/hotspot/Bluetooth when available | Android OS tethering | Usually none |
| Full Laptop Routing | All laptop traffic through phone | Wintun + tun2socks-style layer | Admin routing/DNS changes |

## v0.6 hardening retained baseline

v0.6 keeps the v0.5 fixes and adds a merge/correctness pass based on code-review feedback:

- Preserves bytes over-read after HTTP headers on both Android and Windows.
- Strips `X-TetherBridge-Token`, `Proxy-Authorization`, and other proxy-only headers before forwarding plain HTTP to origin servers.
- Adds Android connection counters and a `/status` endpoint.
- Adds a Windows "View Traffic Status" card that reads the phone bridge counters.
- Uses an owning `NetworkStream` on Windows so relay phone sockets are closed when streams are disposed.
- Adds USB reconnect/re-forward attempts before the Windows app restores proxy state after a USB health-check failure.
- Avoids saving TetherBridge's own temporary proxy as the user's baseline when switching/retrying modes.
- Adds a WinHTTP diagnostics inspector while keeping WinHTTP sync as an explicit future option.

- Uses per-peer authentication backoff without sleeping under a shared lock.
- Explicitly sheds overloaded Android connections with `RejectedExecutionException` handling.
- Keeps Wi-Fi Direct and Bluetooth socket transport as documented roadmap items, not active misleading buttons.
- Adds shutdown/close proxy-restore behavior in the Windows companion.

## Current routing boundary

Proxy Bridge mode changes the current user's WinINet proxy settings. That covers browsers and many
desktop apps, but it is not a full-machine tunnel. Some services, CLI tools, and enterprise software
use separate WinHTTP proxy settings or ignore system proxy settings entirely. WinHTTP sync should remain
an explicit future option with its own restore point.

## Feature-parity status

This build is closer to a dependable paired bridge, but it is still not a full original-feature-equivalent product.
Known remaining parity gaps are tracked in `docs/original-like-feature-parity.md`:

- Real Wi-Fi Direct hotspot/peer mode is not implemented.
- Real Bluetooth RFCOMM data tunneling is not implemented.
- Full laptop routing is still design-only.
- HTTP/1.1 persistent-proxy connection reuse is still limited; the bridge currently favors correctness and cleanup.

## Build notes

- Android: import the Gradle project root into Android Studio.
- Windows: open `windows/TetherBridge.Desktop/TetherBridge.Desktop.csproj` with a .NET 8 SDK on Windows.
- Regression checks: run `powershell -ExecutionPolicy Bypass -File scripts/run-regression-tests.ps1`.
- Full tunnel mode: not implemented in this scaffold; see `docs/full-tunnel-design.md`.

## Important boundaries

- Do not install this as an update over another vendor's app.
- Do not reuse another vendor's package name, icons, certificates, or strings.
- Do not bypass carrier, licensing, or entitlement systems.
- Use the app as an assistive bridge/diagnostic tool and a clean-room implementation.


## v0.6.1 usability overlay

This package applies your v0.6 feature implementation plan on top of the corrected v0.6 baseline. It adds a polished proxy-mode layer on top of v0.6: Windows Status/Pairing/Traffic/Diagnostics/Logs tabs, Android QR pairing payload display, periodic Android `/status` polling, redacted diagnostics export, and clearer Wi-Fi/Bluetooth/PAN guidance. It does not claim Wi-Fi Direct, RFCOMM, or full-tunnel routing are implemented yet.

## v0.6.2 SOCKS5 foundation

v0.6.2 adds a TCP-only local SOCKS5 relay at `127.0.0.1:10810`. It accepts SOCKS5 `CONNECT` requests and translates them to HTTP `CONNECT` through the existing phone bridge with the same pairing token handling. This is not full laptop routing yet: UDP ASSOCIATE, route changes, DNS changes, Wintun, and tun2socks launch are still v0.7+ work. The separate HTTP proxy relay on `127.0.0.1:10809` remains the Windows system proxy path.
