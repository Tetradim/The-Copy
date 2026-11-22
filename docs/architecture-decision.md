# Architecture decision: local relay first, full tunnel second

## Decision

Use a local Windows relay as the stable front door:

```text
Windows apps -> 127.0.0.1:10809 -> local relay -> phone bridge
```

The Android app exposes a bridge endpoint. The Windows app chooses how to reach that endpoint:

- USB: `adb forward tcp:10888 tcp:10888`, then connect to `127.0.0.1:10888`.
- Wi-Fi: connect to `PHONE_IP:10888` using a pairing token.
- Bluetooth/PAN: first detect whether Windows has a Bluetooth PAN adapter and a route to the phone, then connect to `PHONE_PAN_IP:10888`.

## Why this is better than pointing Windows directly at the phone

1. **Cleaner security**: the pairing token stays in the companion instead of being placed in global system proxy settings.
2. **One Windows proxy setting**: Windows always points to `127.0.0.1:10809`; mode changes happen behind the relay.
3. **Better diagnostics**: the relay can distinguish local proxy issues, phone connection issues, token failures, DNS failures, and unsupported applications.
4. **Better cleanup**: settings can be restored even if the phone disconnects.
5. **Upgrade path**: the same local relay can later feed Wintun/tun2socks for full-device routing.

## Recommended product tiers

### Tier 1 — Proxy Bridge

The fastest path to a familiar paired-app product. It uses Windows proxy settings and works with apps that respect WinINet/WinHTTP/system proxy settings.

### Tier 2 — Native Assist

The app detects and guides built-in Android USB tethering, hotspot, and Bluetooth tethering. This is often the best user experience when the OS and carrier allow it.

### Tier 3 — Full Laptop Routing

Advanced mode using a Windows virtual adapter, routing table edits, DNS handling, and a tun2socks-style bridge. This is closest to "everything on the laptop uses the phone," but requires admin privileges and installer discipline.
