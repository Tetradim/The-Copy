# Tether Bridge PdaNet Completion Plan

Implementation roadmap and acceptance criteria | 18 November 2026

## Purpose and starting point

Build a maintainable Windows and Android application that provides the PdaNet capabilities needed by this user, and prove each capability on the actual devices. Feature equivalence means observed behavior and test evidence; undocumented PdaNet internals remain unknown until investigated.

The Samsung tablet has passed isolated USB proxy and DNS tests, and parts of Bluetooth have been tested. Full-computer routing, automatic Windows DNS, Wi-Fi Share, full Wi-Fi Direct operation and broad recovery testing are incomplete. The Windows tunnel components and lease monitor are foundations, not an enabled production tunnel.

The laptop currently relies on PdaNet for internet. Tell the user before any test that could interrupt it. Prefer an isolated Windows VM or spare computer with a separate uplink. Preserve PdaNet’s ADB forward and avoid changing its routes, DNS or proxy settings during ordinary development.

## 1 Establish the baseline and test environment

- Record the PdaNet desktop and Android versions, visible settings, installer behavior and results on the Samsung. Compare observable connection, disconnect, restart and error behavior; label undocumented behavior unknown.
- Prepare an isolated Windows VM or spare computer with a snapshot and independent internet access. Record Windows build, administrator rights, Wi-Fi hardware and driver versions. Prepare a second Android device for disruptive tests.
- Record the test machine’s network state and recovery procedure before mutations. Identify Tether Bridge owned adapters, routes, DNS changes, processes and ADB forwards, and preserve external changes.
- **Done when:** a repeatable baseline and recovery checklist exist and PdaNet remains usable. The OnePlus must be connected and authorized for its live tests.

## 2 Full computer routing

- Integrate the existing Wintun and tun2socks launcher, adapter configurator, route inspector, route lease manager and session supervisor into a concrete Windows runtime. Start with USB and Bluetooth, whose upstream transport does not rely on the tunnel’s IP route.
- Implement least-privilege elevation and a validated helper or service interface. Verify dependency hashes, signatures, protected paths, caller identity and adapter identity before network operations.
- In the isolated environment test adapter addressing, next hops, route metrics and IPv4 and IPv6. Detect public more-specific routes and conflicts; explicitly define LAN and IPv6 behavior. Add safe transport endpoint exclusions before enabling routing over Wi-Fi or PAN.
- Connect live health checks and lease renewal. Persist enough owned state to recover from desktop or helper crashes, restart and partial writes. Remove only owned routes; preserve actionable recovery state if a conflict prevents cleanup.
- **Done when:** applications that ignore proxy settings pass TCP and UDP through the phone; disconnect, process kill, sleep and restart restore the expected network state. Confirm real packet paths and test partial failures and conflicts.

## 3 Automatic Windows DNS

- Start and health-check IPv4 and IPv6 local DNS listeners before applying tunnel DNS. Test UDP, TCP, truncation fallback, occupied port 53, timeout and upstream failure.
- Configure DNS on the owned tunnel interface, recording previous and applied values. Restore safely on disconnect, failed setup, crash and restart without overwriting later external changes.
- Inspect DNS selection across Windows interfaces, IPv6, NRPT, encrypted DNS and browser DNS-over-HTTPS. Define and test the intended policy for each. Do not claim leak prevention without packet-capture evidence.
- **Done when:** ordinary Windows applications resolve through the phone without manual proxy settings, and DNS is restored or a clear recovery action is presented after every tested failure.

## 4 Wi Fi Direct and Bluetooth

- Complete Android Wi-Fi Direct group creation, refresh and removal, including permissions, timeout reconciliation, stale groups and credential display. Add the Windows connection and pairing flow for the real group.
- Protect the Wi-Fi transport endpoint from tunnel routing before sending other traffic into the tunnel. Test connection, IP changes, reconnect, group removal and wrong credentials on real hardware.
- Harden Bluetooth RFCOMM for concurrency, authentication timeouts, disconnects, permission loss and restart. Exercise sustained TCP and UDP, half-close, data slots and reserved health slots.
- **Done when:** Samsung and OnePlus pass connection, traffic, failure and reconnect tests for supported modes; unsupported combinations show a specific explanation and recovery guidance.

## 5 Wi Fi Share

- Select a Windows sharing mechanism supported by the actual adapter and completed bridge. Verify that it accepts the tunnel as upstream and determine whether it would displace existing sharing.
- Add explicit start and stop controls, credentials, status and prerequisite checks. Restore only Tether Bridge owned configuration and reject conflicting sharing state rather than silently changing it.
- In the isolated environment connect a second device and test DHCP, DNS, HTTPS, UDP, multiple clients, teardown, sleep and crash. Record hardware and Windows edition limits.
- **Done when:** a second device reaches the internet through Tether Bridge and the prior Windows sharing state is preserved or a conflict is clearly reported.

## 6 Desktop and Android usability

- Finish installer, upgrade and uninstall, driver and platform-tools handling, dependency verification, version compatibility and recovery instructions. Test clean and interrupted installation, upgrade and uninstall while preserving settings.
- Finish tray controls, startup preferences, auto-connect and reconnect state, clear errors, connection status, diagnostics and support export. A successful ADB forward alone must never be reported as a healthy bridge.
- Observe and compare PdaNet’s throttle and Block Microsoft Connections options before implementing defined equivalents. Investigate Hide Tether Usage separately; do not claim equivalent carrier-accounting behavior without reproducible evidence.
- **Done when:** a user can install, connect, diagnose, disconnect, upgrade and uninstall without developer tools, with truthful status and recoverable errors.

## 7 Device and failure test matrix

- Devices: Samsung Galaxy Tab A9+ 5G SM-X218U and OnePlus 13R CPH2647, both Android 16. Test available transports separately, multiple devices and selection of the intended device.
- Applications: browser HTTPS, an application that ignores proxy settings, Windows DNS, TCP and UDP clients, large transfers, concurrent sessions and long idle sessions. Record packet paths, results, versions and logs.
- Failures: USB unplug/replug, device sleep, Android service stop, Bluetooth interruption, Wi-Fi group loss, Windows sleep/resume, desktop or helper kill, Windows restart, occupied ports, changed settings and partial cleanup.
- **Done when:** every matrix row has pass, fail or unsupported status with evidence; failures are fixed and rerun. Give the required advance notice before disruptive tests on the active connection and ensure a recovery path is available.

## 8 Release and acceptance

- Run desktop relay and regression tests, Android unit tests and release builds, installer verification, security review and the live device matrix. Include dependency notices and verify provenance and signing, or clearly disclose signing limits.
- Publish an explicit comparison: implemented and verified, partial, unsupported and unknown. Document differing behavior and supported Windows and Android versions.
- **Done when:** full routing, DNS, Wi-Fi Share, supported transports and required recovery cases pass their acceptance tests. Preserve a path back to PdaNet if the new connection fails.

## Project locations and verification

Project: the repository root. Windows components: windows/TetherBridge.Desktop/FullTunnel, Services, Transports and MainForm.cs. Android components: android/app/src/main/java/org/tetherbridge/classicapp. Installer: installer/install.ps1 and scripts/package-windows.ps1. Desktop integration tests: tests/RelayTests/Program.cs.

Run the desktop suite with the configured .NET 8 SDK: dotnet run --project tests/RelayTests/RelayTests.csproj. Run regression checks: python scripts/run-regression-tests.py. With the configured JDK and Android SDK, run: gradlew.bat :android:app:testDebugUnitTest :android:app:assembleDebug --no-daemon.

Passing these existing commands alone does not establish driver-backed routing, Wi-Fi Share or real-device recovery. Add scenario-specific tests before implementation and record real Windows and device results.

## References

Local evidence: docs/pdanet-desktop-comparison.md and docs/parity-progress.md in the project. PdaNet Wi-Fi Share: https://pdanet.co/help/wifi.php. PdaNet modes: https://pdanet.co/help/devices.php. USB troubleshooting: https://pdanet.co/help/usb.php. Tun2socks Windows example: https://github.com/xjasonlyu/tun2socks/wiki/Examples.
