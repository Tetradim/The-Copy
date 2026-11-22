# Security model

## Goals

- Do not expose an unauthenticated phone proxy on public networks.
- Do not put secrets in Windows global proxy settings.
- Do not leak traffic silently when the phone disconnects.
- Do not leave proxy/route/DNS settings modified after disconnect.
- Do not imply WinHTTP or service traffic is protected by WinINet-only proxy mode.

## Pairing

- USB mode can allow loopback-only no-token operation because ADB forward reaches `127.0.0.1` on the phone.
- Wi-Fi and Bluetooth/PAN modes require a persistent random token shown on the Android app; users can rotate it.
- Wi-Fi and Bluetooth/PAN use TLS 1.2 or later with a pinned per-installation Android keystore certificate. Pair the token and fingerprint over a trusted channel.

## Logs

- Redact tokens, IP addresses if requested, URLs, and hostnames by default.
- Keep diagnostic codes stable so users can share support reports without exposing browsing data.

## Full tunnel risk

Full tunnel mode can see a user's traffic metadata. The app should include a local-only privacy statement and avoid sending diagnostic telemetry unless the user explicitly exports it.

## WinHTTP boundary

The Windows companion currently changes current-user WinINet proxy settings only. Machine-wide WinHTTP
proxy settings are separate and are read by some services, CLI tools, and enterprise software. Until
WinHTTP sync exists with a dedicated snapshot and restore flow, the app must describe proxy mode as
browser-style traffic coverage rather than full laptop routing.


## v0.5 security notes

- Plain HTTP forwarding strips `X-TetherBridge-Token` and proxy-only authorization headers before contacting origin servers.
- Wireless modes require the token and certificate-pinned TLS; interface allowlisting remains future work.
- The Windows relay remains bound to `127.0.0.1` only.
- WinHTTP is inspected, not modified, so future WinHTTP sync must have its own explicit restore point.
