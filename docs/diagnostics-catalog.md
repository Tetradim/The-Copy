# Diagnostics catalog

Every diagnostic should have:

- stable code
- plain-English diagnosis
- exact failing probe
- safe action button
- cleanup action when relevant

## Core errors

| Code | Message | Probe | Likely fix |
|---|---|---|---|
| `ADB_NOT_FOUND` | ADB is not installed or not on PATH. | Run `adb version`. | Bundle platform-tools or ask user to install them. |
| `ADB_NO_DEVICE` | No Android device is visible over USB. | Run `adb devices`. | Check USB cable, unlock phone, set USB mode. |
| `ADB_UNAUTHORIZED` | Phone is connected but has not authorized this computer. | Parse `adb devices` state. | Revoke/approve USB debugging prompt. |
| `ADB_FORWARD_FAILED` | USB port forwarding could not be created. | Run `adb forward tcp:10888 tcp:10888`. | Kill stale adb server, retry, check port conflict. |
| `PHONE_PROXY_DOWN` | Phone bridge service is not reachable. | HTTP GET `/health`. | Start Android bridge service. |
| `TOKEN_REJECTED` | Phone rejected the pairing token. | HTTP GET `/status` with token. | Re-pair with current token. |
| `LOCAL_PROXY_DOWN` | Windows local relay is not listening. | TCP connect `127.0.0.1:10809`. | Restart Windows companion. |
| `WININET_PROXY_STALE` | Windows proxy is still enabled after disconnect. | Read current user proxy config. | Restore saved config. |
| `WINHTTP_PROXY_MISMATCH` | Some Windows services may not use the same proxy. | `netsh winhttp show proxy`. | Offer optional WinHTTP sync, with restore point. |
| `DNS_UNSUPPORTED_APP` | App is likely bypassing system proxy or doing its own DNS. | Compare browser probe vs raw socket probe. | Use native tethering or full tunnel mode. |
| `BLUETOOTH_PAN_UNAVAILABLE` | Windows does not show a Bluetooth PAN path to the phone. | Enumerate network adapters/routes. | Pair device and enable Bluetooth tethering. |
| `FULL_TUNNEL_NEEDS_ADMIN` | Full routing requires administrative rights. | Check elevation. | Relaunch elevated or use proxy mode. |
| `WINTUN_MISSING` | Virtual adapter driver is missing. | Try loading Wintun DLL/adapter. | Install signed driver bundle. |
| `ROUTE_RESTORE_NEEDED` | Default route/DNS state was changed and must be restored. | Compare state snapshot. | Restore route/DNS snapshot. |

## Positive diagnostics

The app should also report what is working:

- `ADB_OK`
- `PHONE_AUTHORIZED`
- `USB_FORWARD_OK`
- `PHONE_PROXY_OK`
- `LOCAL_PROXY_OK`
- `WINDOWS_PROXY_ENABLED`
- `BROWSER_PROBE_OK`
- `NATIVE_TETHER_DETECTED`
- `PAN_ROUTE_OK`


## v0.5 diagnostics

| Code | Meaning | Fix |
|---|---|---|
| PHONE_STATUS_OK | Android bridge returned `/status` counters. | No action needed. |
| PHONE_STATUS_DOWN | `/status` could not be reached. | Start the Android bridge and reconnect. |
| USB_RECONNECT_ATTEMPT | Windows is trying to recreate `adb forward` before restoring proxy state. | Reconnect/authorize USB if it fails. |
| WINHTTP_PROXY_OTHER | WinHTTP has a separate proxy config not managed by TetherBridge. | Use WinINet apps, manually manage WinHTTP, or wait for explicit WinHTTP sync. |
