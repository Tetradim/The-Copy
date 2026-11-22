# Feature matrix

| Feature | Proxy Bridge | Native Tether Assist | Full Laptop Routing |
|---|---:|---:|---:|
| No root required | Yes | Yes | Yes |
| Works over USB | Yes | Yes if OS allows | Yes |
| Works over Wi-Fi | Yes | Yes if hotspot allowed | Yes |
| Works over Bluetooth/PAN | Maybe | Yes if phone/PC supports PAN | Maybe, if PAN is stable |
| Requires Windows admin | No for proxy, yes for some repairs | Usually no | Yes |
| Requires driver | No | No | Usually yes, Wintun or equivalent |
| All browser traffic | Usually | Yes | Yes |
| All laptop traffic | No | Yes | Yes |
| UDP support | No or limited | Yes | Yes if implemented |
| DNS control | Limited | Native OS | Required |
| Good diagnostics | Excellent | Excellent | Hard but possible |
| Installer complexity | Low | Low | High |
| Most like classic paired apps | High | Medium | Highest for seamless routing |

## Product recommendation

Ship in this order:

1. **Proxy Bridge + Native Assist** as v1.0.
2. **Wi-Fi and Bluetooth/PAN polish** as v1.1.
3. **Full Laptop Routing beta** as v2.0.

This gets a useful product into users' hands while avoiding a fragile first release that depends on drivers and route rewrites.


## v0.5/v0.6 additions

- Traffic status endpoint and Windows status viewer.
- Header over-read preservation on Android and Windows.
- Token-header stripping before plain HTTP origin forwarding.
- USB reconnect/re-forward attempt before proxy restore.
- WinHTTP diagnostics inspector.


## v0.6 UI truthfulness check

| Item | Status |
|---|---|
| Misleading future-mode buttons | Removed / not exposed |
| Real Wi-Fi Direct transport | Not implemented |
| Real Bluetooth socket transport | Not implemented |
| Existing Wi-Fi Bridge | Implemented as phone-IP/token bridge |
| Bluetooth/PAN Assist | Implemented as diagnostics/assist, not custom data path |


## v0.6.1 additions

| Feature | Status | Notes |
|---|---|---|
| Live Windows traffic/status panel | Implemented in source | Combines local relay counters and Android `/status` polling. |
| Android QR pairing display | Implemented in source | Uses clean-room `tetherbridge://pair` payload. |
| Windows pairing from payload | Implemented in source | Paste QR payload/manual text into Pairing tab. |
| Redacted diagnostics export | Implemented in source | Exports logs, diagnostics, and status with token redaction. |
| Wi-Fi Direct group formation | Not implemented | UI explicitly says this is future work. |
| Bluetooth RFCOMM tunnel | Scaffold only | PAN assist remains diagnostics/guidance only; no exposed socket data path yet. |


## v0.6.1 overlay

| Capability | Status |
|---|---|
| Live traffic/status panel | Implemented in Windows UI with local relay + Android `/status` polling |
| QR pairing | Android QR generation implemented; Windows paste/manual parser implemented |
| Wi-Fi/hotspot assist | Implemented as diagnostics/guidance, not real Wi-Fi Direct group formation |
| Bluetooth/PAN assist | Implemented as diagnostics/guidance; RFCOMM transport scaffold only |
| Support bundle export | Implemented with token redaction |
| Full laptop routing | Deferred |


## Wintun/tun2socks v0.7 research

Full Laptop Routing remains deferred, but the target architecture is now more concrete: an elevated Windows service creates a Wintun adapter, manages route/DNS snapshots, launches a tun2socks-style engine, and routes through the existing local relay once SOCKS5 support is added.
