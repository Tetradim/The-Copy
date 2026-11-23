# Observed PdaNet desktop feature comparison

Inspection date: 2026-11-17. The desktop labels below were observed in the user's installed `PdaNetPC.exe` and are evidence of exposed options, not evidence of their internal implementation. No PdaNet executable was modified or executed for this inventory. PdaNet's published help is cited for behavior it describes.

| Observed option | Current TetherBridge status |
| --- | --- |
| Connect USB; Disconnect | USB proxy bridge exists; end-to-end whole-computer routing is still missing. |
| Connect WiFi; WiFi Pairing | Existing-network TLS/token pairing exists. Android Wi-Fi Direct group control is under development and has not passed a physical Windows-to-phone test. |
| Connect Bluetooth; Bluetooth Pairing | Paired RFCOMM transport is under development. Samsung tests have exercised health, HTTPS, DNS and half-close, but not all failure and multi-device cases. |
| Auto Connect on USB Plug-in | Opt-in startup and USB hot-plug auto-connect are implemented; real unplug/replug and resume tests remain open. |
| WiFi Share; Windows Hotspot Settings | Windows sharing of the bridged connection remains unimplemented. [PdaNet's help](https://pdanet.co/a/wifi/others.php) describes sharing a USB connection through Windows. |
| Settings; Throttle Connection | An opt-in bandwidth limit now paces aggregate bridge traffic per direction; matching PdaNet throttle semantics and broader load tests remain open. |
| Settings; Block Microsoft Connections | Unimplemented; exact scope and behavior have not been verified. |
| Online Help; About | Diagnostics exist, but equivalent help and about flows remain open. |
| Hide Tether Usage (phone option) | Unimplemented and not equivalent to simply using a proxy. [PdaNet's published guidance](https://pdanet.co/help/devices.php) mentions the option, but does not specify its mechanism. Do not label TetherBridge as hiding or reclassifying carrier usage without evidence. |

The user-supplied installer is `PdaNetA5232b.exe`; the installed binaries used for this read-only inventory are under `C:\Program Files (x86)\PdaNet for Android`. String presence alone does not prove the feature works on the user's current phone or reveal all settings and behavior.

## USB failure investigated on Windows 0.6.12 and Android 0.6.8

Read-only inspection of the installed PdaNet 5.23.2 desktop files found WinUSB pipe enumeration and read/write calls, ADB forwarding commands targeting a TCP port and a local abstract socket, and an NDIS miniport in pneteth.sys. This establishes the presence of a direct USB path, an ADB-capable path, and a Windows virtual adapter. It gives no measurement of PdaNet's connection limit, scheduling, or stream multiplexing. Seeing ADB support in the binary does not establish which path a particular PdaNet connection uses.

On the Samsung USB link, a framed health request returned HTTP 200 with no load, returned EOF when 32 data sockets were held open, and returned HTTP 200 after one socket closed. This reproduces the symptom with TetherBridge's shared Android worker pool. Windows 0.6.13 queues data at 28 concurrent sessions and bypasses that queue for serialized health and status probes. Android 0.6.9 logs framed worker saturation. Long playback and real Bluetooth approval still require a live retest.
