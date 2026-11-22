# Release roadmap

## v0.6 corrected base

- Android/Windows header over-read preservation.
- Per-peer auth backoff.
- Android overload connection shedding.
- Reversible proxy restore on session loss and app close.
- No fake Wi-Fi Direct or Bluetooth socket buttons.

## v0.6.1 proxy usability overlay

- Windows Status / Traffic / Pairing / Diagnostics / Logs tabs.
- Android QR pairing payload display.
- Windows manual/paste pairing parser.
- Periodic phone `/status` polling.
- Redacted support-bundle export.
- Wi-Fi/hotspot/PAN guidance and inspection.
- Bluetooth RFCOMM and Wi-Fi Direct kept as honest scaffolds/docs only.

## v0.6.2 recommended next polish

- Add real build validation on Windows with .NET 8 SDK.
- Add Android Studio/Gradle build validation.
- Add persistent settings for last phone IP/token alias.
- Add a connection-history list.
- Add QR webcam scanning only if a low-risk dependency is chosen.

## v0.7 full-routing beta

- Elevated `FullTunnelService`.
- Wintun adapter creation/removal.
- tun2socks lifecycle management.
- SOCKS5 support in the relay/phone bridge.
- Route/DNS/interface snapshot + restore.
- Phone transport route protection.
- Clear full-tunnel warning UI.

## v1.0 stable target

- Signed Windows installer.
- Signed Android APK.
- Clean update manifest.
- Support bundle import/export and privacy review.
- Device-level USB/Wi-Fi/PAN test matrix.
