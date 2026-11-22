# Maintainer notes: USB proxy test

This source includes the Android listener isolation fix and a SOCKS5 authentication negotiation fix. It is a debug/test version; it is not a full-device tunnel or a complete PdaNet replacement.

## Build

Prerequisites: JDK 17, Android SDK with API 35, and .NET SDK 8. The project includes a Gradle wrapper. Set ANDROID_HOME to the SDK path. From this folder:

    .\gradlew.bat :android:app:assembleDebug
    dotnet build windows/TetherBridge.Desktop/TetherBridge.Desktop.csproj -c Release
    dotnet run --project tests/RelayTests/RelayTests.csproj
    python scripts/run-regression-tests.py

The SOCKS test uses only loopback sockets and does not alter Windows proxy or routes.

## Isolated live USB test

Connect one authorized Android device with TetherBridge installed and start USB Bridge in the Android app. Ensure ports 19088-19090 are unused. The following commands exercise the internal direct HTTP proxy, not the framed path used by the desktop app. They create a temporary ADB forward and do not set the Windows system proxy:

    adb forward --no-rebind tcp:19088 tcp:10890
    dotnet run --project tests/RelayTests/RelayTests.csproj -- live

While that program is running, in another terminal:

    curl.exe --proxy http://127.0.0.1:19089 https://example.com/
    curl.exe --socks5-hostname 127.0.0.1:19090 https://example.com/

The relay exits after 45 seconds. Remove only the test forward with `adb forward --remove tcp:19088` and stop the Android bridge using its Stop Bridge button. When multiple devices are attached, select the desired device with adb -s SERIAL for forwarding.

## Remaining work

The desktop application changes the Windows current-user proxy when its connection button is used. This does not tunnel all applications. Full-device routing, system-wide DNS configuration, crash-safe restoration, Bluetooth data transport, long-duration reconnect testing, and OnePlus testing remain unfinished. SOCKS5 UDP is available for applications explicitly configured to use it. The Wi-Fi/PAN bridge uses certificate-pinned TLS and requires a current Android pairing payload. Treat this as test software.

## Android half-close investigation

`tests/DeviceHalfClose.java` exercises the Android HTTP CONNECT proxy using two local sockets on the device. On the Samsung tablet the updated bridge returned the response after the client closed its sending direction. Direct unframed ADB forwarding fails this test. The current 0.6.4 framed transport passed it across Windows, ADB, and Android. The Windows HTTP and SOCKS relays now preserve both directions after an orderly half-close; loopback integration tests cover both paths. Other TCP behaviors and long-running sessions still require broader testing. The comparison with the previous Android build was inconclusive because the Android test process was terminated.

## USB forward ownership

The Windows USB session asks ADB to allocate an available local TCP port instead of using a fixed port. The selected device and endpoints are recorded in memory. On orderly shutdown or a failed phone health check, TetherBridge checks the ADB forward list and removes only the matching mapping it created. Recovery uses `--no-rebind`, so it will not replace a mapping already held by another tool. A crash or an ADB error can still leave a forward behind; check `adb forward --list` before removing any mapping. This does not yet make recovery or ownership atomic against other programs changing ADB forwards concurrently.

Run the isolated allocation and cleanup integration test with one authorized Android device connected:

    dotnet run --project tests/RelayTests/RelayTests.csproj -- adb-live

This test does not change Windows proxy settings or routes.

## Latest reliability changes

The Android proxy now validates CONNECT authorities including bracketed IPv6, synchronously binds its listener, reports startup failures to the app screen, and restores the saved active mode on a sticky service restart. A forced-process-crash test on the Samsung tablet confirmed Android restarted the service in USB mode and the health endpoint responded. The Windows HTTP and SOCKS relays support TCP half-close in loopback tests; the framed 0.6.4 USB path passed an end-to-end half-close test across Windows, ADB, and the Samsung tablet. Wi-Fi/PAN requires certificate-pinned TLS and a valid token; pairing must be obtained through a trusted channel. Full-device routing and Bluetooth socket transport remain unimplemented.

## Wireless TLS compatibility

Wi-Fi and PAN now use TLS 1.2 or newer with a device keystore certificate. The current Android pairing URI contains its SHA-256 fingerprint and token; the Windows companion refuses wireless connections without the fingerprint. Old pairing URIs and older clients are incompatible: update both sides and pair again. Pairing payloads contain a secret and must only be transferred through a trusted channel. Android/Windows TLS pinning, incorrect-pin rejection, plaintext rejection, incorrect-token rejection, and HTTPS through HTTP and SOCKS relays passed on the Samsung tablet over an isolated ADB forward. The actual Wi-Fi network path still requires device testing. For an isolated test using an ADB forward while Wi-Fi Bridge is active, run `python tests/device_tls_probe.py`; no system proxy settings are changed.

## 0.6.4 framed USB transport

Current Windows USB mode requests a dynamic ADB forward to the Android loopback-only port 10889. Each connection uses big-endian 32-bit frame lengths, with data frames limited to 32768 bytes and a zero-length frame representing write EOF. This keeps the ADB TCP stream open through a half-close. Android 0.6.4 works with Windows 0.6.4 or later versions that support the framed USB protocol. Android 0.6.5 uses loopback port 10890 for the internal USB HTTP proxy and port 10888 for encrypted Wi-Fi/PAN, avoiding mode-switch port conflicts. The manual diagnostics above use the direct USB proxy and do not exercise the framed transport.

The Windows relay, ADB, and Samsung Android 16 tablet passed HTTPS, SOCKS HTTPS, and a response-after-half-close integration test. The test executable `tests/device_framed_probe.py` requires the Android USB bridge running and the `DeviceEofServer` fixture running on the tablet; it creates and removes its own ADB forward. Crash recovery, long sessions, other Android devices, and complete application coverage remain to be tested.

## Selecting an Android device

Windows 0.6.5 adds an Android device list on the Status tab. Refresh the list and choose the tablet or phone when multiple devices are connected. Automatic selection works only when exactly one authorized device is ready. A selected device that is missing or unauthorized is not silently replaced by another device. USB setup, pairing injection, recovery, diagnostics and forward cleanup target the chosen device; changing the dropdown affects the next connection, not an active session. The multi-device behavior has been tested with simulated ADB responses; a live two-device test remains outstanding.

## SOCKS5 UDP support

Windows 0.6.6 and Android 0.6.5 implement SOCKS5 UDP ASSOCIATE for explicitly configured clients. The local association uses loopback, pins the first valid client UDP endpoint, and closes with its TCP control connection. It drops fragmented SOCKS packets, limits each association to 32 destination flows, and bounds each datagram at 65507 bytes. A destination flow sends authenticated `CONNECT-UDP` over the existing USB framed connection or pinned TLS connection; each UDP payload is encoded as a four-byte big-endian length followed by the payload. A zero-length payload is an empty datagram, not an end marker. Android sends packets through the tablet network and only forwards replies from the chosen destination.

Windows enables UDP only when the Android status advertises the `udp` capability. With an older Android build, TCP remains available and SOCKS UDP ASSOCIATE is explicitly rejected. On the Samsung Android 16 tablet, small, empty, and 40000-byte echo datagrams and a DNS query passed over framed USB; the same protocol passed over pinned TLS carried through an isolated ADB forward. The physical Wi-Fi/PAN route, other devices, extended load, and full system routing remain unverified. No routes, DNS configuration, or adapters were changed by the tests.
