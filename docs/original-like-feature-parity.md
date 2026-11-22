# Original-like feature-parity roadmap

This project stays clean-room: it tracks public/user-visible capabilities, not private implementation details.

## Current status

| Capability | v0.6 status | Notes |
|---|---|---|
| USB bridge through ADB forwarding | Implemented scaffold | Closest current parity path. |
| Windows local relay | Implemented scaffold | Safer than pointing Windows directly at the phone. |
| Reversible WinINet proxy settings | Implemented scaffold | Includes snapshot pruning and duplicate-baseline protection. |
| Wi-Fi bridge over existing reachable IP network | Implemented scaffold | Requires phone IP + token. |
| Wi-Fi Direct / phone-created peer network | Not implemented | Next major original-like gap. |
| Bluetooth/PAN assist | Implemented diagnostics | No custom Bluetooth data path yet. |
| Bluetooth RFCOMM/socket bridge | Not implemented | Next major original-like gap. |
| Traffic viewer | Basic `/status` counters | Needs richer per-connection UI. |
| Full laptop routing | Design only | Requires virtual adapter, DNS, route restore, and installer/service work. |

## Recommended order

1. Finish v0.6 correctness and build verification.
2. Add a real Wi-Fi Direct controller and Windows pairing flow.
3. Add Bluetooth RFCOMM bridge as an alternative transport to the same relay protocol.
4. Expand traffic status from aggregate counters into a connection table.
5. Add optional full laptop routing with a signed installer/service.

## Wi-Fi Direct design target

Android should create or join a peer group, show group owner IP and a short-lived pairing token, and expose the same bridge service on the group-owner interface only. Windows should treat it as another `IPhoneTransport` rather than a separate proxy architecture.

## Bluetooth RFCOMM design target

Android should expose a Bluetooth service UUID and the same bridge framing over an RFCOMM stream. Windows should connect through a `BluetoothRfcommTransport` that implements `IPhoneTransport`. The local Windows relay should not care whether the upstream is USB, Wi-Fi, PAN, or RFCOMM.

## Traffic viewer design target

The Android `/status` endpoint should grow from aggregate counters into:

- active connection count,
- total connection count,
- bytes up/down,
- recent errors,
- last client address,
- uptime,
- mode,
- transport security state.
