# Full Laptop Routing design

Full Laptop Routing is deferred to v0.7+. v0.6.2 adds the first TCP-only SOCKS5 local relay foundation, but full routing still touches drivers, routes, DNS, admin elevation, and crash recovery.

## Recommended v0.7+ architecture

```text
Windows apps
  -> Windows TCP/IP stack
  -> Wintun virtual adapter
  -> tun2socks-style userspace TCP/IP engine
  -> local SOCKS5 relay on 127.0.0.1:10810
  -> USB/Wi-Fi/PAN phone transport
  -> Android bridge service
  -> Android outbound network
```

## Wintun integration investigation

Wintun is the recommended clean-room Windows adapter target because it is a Layer-3 TUN driver designed for userspace packet processing. The Windows UI should not create or route the adapter directly. Use a small elevated Windows service for adapter, route, DNS, and cleanup operations, while the desktop app remains unelevated and talks to the service.

Recommended service split:

| Component | Responsibility | Privilege |
|---|---|---:|
| Desktop UI | Mode selection, status, logs, pairing, support export | User |
| Local relay | Existing proxy bridge endpoint and stats | User |
| FullTunnelService | Wintun adapter, routes, DNS, tun2socks lifecycle | Admin/service |
| Recovery task | Restore network state after crash/reboot | Admin/service |

## tun2socks integration target

Use a tun2socks-style engine rather than writing a TCP/IP stack from scratch. The preferred path is to launch or embed a maintained Go tun2socks engine and point it at the local relay/SOCKS endpoint. For TetherBridge this requires adding SOCKS5 support to the local relay/Android bridge before full tunnel becomes production-ready.

Example target command shape, not a final shipped command:

```powershell
tun2socks --device <wintun-adapter-or-guid> --proxy socks5://127.0.0.1:10810
```

## Required components

1. **Virtual adapter**
   - Wintun or equivalent signed Layer-3 virtual adapter.
   - Created/removed by `FullTunnelService`, not the normal UI process.

2. **Route manager**
   - Snapshot default routes, DNS, interface metrics, IPv6 state, and existing proxy state before changes.
   - Add a protected split route for the active phone transport so phone reachability does not route into the tunnel.
   - Route default traffic into the tunnel adapter.
   - Restore everything on disconnect, crash recovery, service restart, and app close.

3. **DNS manager**
   - Do not assume tun2socks solves DNS by itself.
   - Provide a local DNS proxy or set DNS servers deliberately.
   - Capture and restore DNS configuration.

4. **UDP and IPv6 policy**
   - HTTP CONNECT is TCP-only.
   - Full routing needs SOCKS5 UDP ASSOCIATE, a custom UDP relay, or a clear user-visible limitation for QUIC/games/DNS.
   - IPv6 should either be tunneled intentionally or disabled/restored intentionally for the tunnel session.

5. **Kill switch**
   - Optional for v0.7 beta, but state must be explicit.
   - If enabled, block traffic when the phone bridge disconnects so Windows does not silently fall back to another interface.

6. **Installer and trust**
   - MSI/WiX or similar installer recommended.
   - Bundle third-party binaries only from official releases, verify hashes, and document licenses.

## Biggest risks

- Breaking user networking if route/DNS restore is wrong.
- Elevation, installer reputation, and antivirus/Defender friction.
- Sleep/resume, USB unplug/replug, and phone IP changes.
- UDP, IPv6, DNS-over-HTTPS, and apps with their own VPN/proxy stacks.

## v0.7 acceptance criteria

- `FullTunnelService` can create and remove a virtual adapter.
- State snapshots include routes, DNS, proxy, and interface metrics.
- Phone transport route stays outside the tunnel.
- Disconnect restores all snapshots.
- Support bundle includes redacted route/DNS/service logs.
- Full Tunnel UI warns that admin/network-driver changes are active.


## v0.7 Wintun/tun2socks investigation overlay

Wintun is the preferred Windows Layer-3 adapter candidate for the future Full Laptop Routing mode. The normal desktop UI should not perform Wintun, route, DNS, or firewall operations directly; those belong in an elevated service.

A practical v0.7 path is:

```text
Windows UI -> elevated FullTunnelService -> Wintun adapter -> tun2socks -> local SOCKS5 relay -> Android bridge
```

Important constraint: v0.6.2 exposes a TCP-only SOCKS5 endpoint, not a production UDP/full-routing bridge. Before tun2socks can be wired as a real data path, the project still needs DNS/UDP policy, route restore, Wintun service ownership, and real device testing. Until then, Wintun/tun2socks stays documented as the next-phase plan, not an enabled feature.

## Wintun/tun2socks investigation overlay for v0.7+

This is a design-only track; v0.6.2 does not create adapters, change routes, change DNS, or install drivers.

### Recommended implementation shape

```text
Windows applications
  -> Windows TCP/IP stack
  -> TetherBridge Wintun adapter
  -> tun2socks/gVisor userspace TCP/IP engine
  -> SOCKS5 endpoint on local relay: socks5://127.0.0.1:10810
  -> phone bridge transport
  -> Android outbound network
```

### Dependency policy

- Use official Wintun distribution only (`wintun.dll` beside the elevated service/engine), not copied driver files from another app.
- Treat `tun2socks` as an external subprocess or separately vendored component with explicit license review.
- Keep the normal Windows Forms UI unelevated; privileged adapter, route, DNS, and firewall work belongs in a small elevated service.
- Store a crash-recovery snapshot before changing route, DNS, interface metric, WinINet, or WinHTTP state.

### v0.7 service skeleton targets

1. `FullTunnelService` runs elevated and owns Wintun/tun2socks lifecycle.
2. UI sends start/stop/status commands through a local named pipe or loopback control channel.
3. Service verifies proxy-mode phone health before touching routes.
4. Service starts local relay with SOCKS5 support, then launches/configures tun2socks.
5. Service assigns tunnel IP/DNS, adds a protected host route for the phone transport, and only then adjusts default routes.
6. Service monitors the phone bridge, Wintun session, tun2socks process, DNS probe, and route table.
7. Service restores every saved network setting on explicit stop, failed start, crash-repair startup, or phone disconnect.

### Do not ship until these are real

- SOCKS5 TCP support in the local relay is present; UDP support remains open.
- DNS policy that handles Windows resolver behavior deliberately.
- IPv6 route policy.
- UDP support plan for QUIC/games/DNS.
- Kill-switch behavior that is opt-in and reversible.
- Installer/service uninstall recovery tests.

## Windows tunnel lab

The Windows tunnel lab connects the existing route supervisor to the real
Windows adapter backend. It starts a loopback DNS listener on port 53 and
checks TCP and UDP on both loopback address families before adding leased
IPv4 and IPv6 routes. It checks route ownership after setup and renewal.
The normal desktop app still uses proxy mode.

Use a disposable Windows test system with a separate uplink, the protected
Wintun/tun2socks dependencies, and a test Android device running USB Bridge.
Before the lab run, capture the route table, DNS configuration and adapter
inventory. Check routes without making changes:

    dotnet run --project windows/TetherBridge.TunnelLab -- --check-routes

In a standard user terminal, start the dedicated SOCKS relay with the
serial reported by ADB. Keep that terminal open:

    dotnet run --project tests/RelayTests -- usb-lab-relay SERIAL

Build the lab and run the built executable from a protected location in an
administrator terminal. Replace UPSTREAM_DNS_IP with the reachable resolver
used by the test environment:

    TetherBridge.TunnelLab.exe --isolated-network-test UPSTREAM_DNS_IP

Verify non-proxy TCP and UDP plus Windows DNS with route inspection and a
packet capture. Press Ctrl+C in the lab terminal, compare the resulting
network configuration with the captured baseline, then stop the relay.
Run sleep and forced-exit scenarios separately on the disposable system.
The lab has no durable journal or Windows sharing integration. Real route
leases, DNS selection across interfaces, and crash recovery remain unverified.

Before and after each test, use the lab's --capture-state <new-file> and
--compare-state <baseline-file> commands to compare adapter IDs, DNS servers
and routes. Route lifetimes are deliberately excluded because they expire
over time. Investigate differences before marking recovery as passed.

The read-only --check-sharing <adapter-GUID> command checks the Windows
Mobile Hotspot capability of a specific upstream adapter. Use the private
tunnel adapter's GUID from the snapshot. The check does not create a
hotspot or verify that a connected client can access the internet.

To include Windows Mobile Hotspot in the isolated lab run, add --share-wifi
to the tunnel command. Windows must expose a connection profile for the
tunnel adapter and permit Mobile Hotspot for that exact profile. The lab
requires the hotspot to be off at start and uses the existing hotspot
name and password. It requests hotspot shutdown before route teardown.
Windows hotspot controls are system-wide, so avoid simultaneous changes
from Windows Settings during the lab. A failed or interrupted start may
require checking hotspot state in Settings.

Join from another device, then verify DHCP, DNS, TCP and UDP. Confirm
the client exits via the phone and compare network state after teardown.
Hotspot startup and client traffic are not yet verified. A device with
an enabled hotspot on a different upstream does not establish support
for sharing the Wintun adapter.

- Tunnel Lab now launches each route and adapter command in a Windows job.
  On timeout or cancellation it closes the job and waits for netsh to exit
  before rollback runs. A process exit code replaces captured netsh output
  in error messages. The isolated read-only netsh and process cancellation
  checks can be rerun with:

      dotnet run --project tests/RelayTests -- tunnel-command-test

Use --check-usb-dns <Android-serial> <resolver-IP> to run the same port 53
DNS preflight without a tunnel adapter or Windows networking changes.
It requires an authorized ADB device running Tether Bridge in USB mode
and free IPv4 and IPv6 loopback DNS ports. The check allocates and releases
its own ADB forward and starts a SOCKS relay on a dynamically assigned port.

The local integration check exercises the same DNS probe over all four
loopback paths and rejects an upstream failure response:

    dotnet run --project tests/RelayTests -- tunnel-dns-preflight-test

Use --verify-tunnel-https <adapter-GUID> <destination-IP> <https-URL> from a
second terminal while the tunnel lab is connected. Supply an address for
the HTTPS server named in the URL. Windows must prefer the tunnel adapter
for that address and the connection is bound to the tunnel source IP.
The probe disables Windows proxies and redirects, and validates TLS for
the supplied hostname. A successful result is evidence of direct HTTPS
for that address. Test system name resolution and UDP separately.

## Section 3 USB measurements

Use a controlled endpoint reachable by the phone over its upstream
connection. Run tests/usb_benchmark_server.py there with a private
bind address and a fixed port. Its upload endpoint discards the
payload and its download endpoint generates fixed-size test data.
Keep it on a private or otherwise access-controlled test host.
The default is local loopback and an automatically chosen port.

Run the same origin, transfer size and concurrency for each path:

    dotnet run --project windows/TetherBridge.TunnelLab -- --benchmark-bridge-usb SERIAL http://SERVER:PORT/ 16 4
    dotnet run --project windows/TetherBridge.TunnelLab -- --benchmark-usb http://SERVER:PORT/ 16 4 direct ADAPTER_INDEX

Use the first command for framed ADB USB. For the second command,
use the native USB adapter index in the native run and the tunnel
adapter index in the Tunnel Lab run. Get interface indices from
Get-NetAdapter or the lab baseline. The direct benchmark checks that
Windows prefers that interface for the endpoint before and after each
transfer phase and bypasses the system proxy. Use a test window with
an alternate connection before changing native tethering or tunnel
routes. Keep the cable, port, endpoint and phone uplink comparable.

Run three repetitions per path. Save the JSON output. It measures
single-stream and concurrent upload/download, idle and loaded latency,
and test-process CPU. The process CPU figure excludes the kernel,
ADB server and phone, and is not a total system CPU comparison.
Loaded latency p95 is suppressed for phases with fewer than five samples.

With the normal USB desktop session connected, measure the duration
from first observed request failure to first successful new request:

    dotnet run --project windows/TetherBridge.TunnelLab -- --measure-usb-recovery http://SERVER:PORT/ 10810

Start this check before unplugging. It uses fresh SOCKS requests and
does not reconnect ADB itself. The observed interval includes any time
the cable stays unplugged. Record that interval separately if comparing
application recovery.

A controlled Samsung smoke test sent traffic through framed USB and
used a temporary ADB reverse mapping back to the benchmark server.
That crosses the cable twice and uses no mobile uplink. It confirms the
measurement path, not real Internet speed or superiority over native USB.
Do not adopt frame packing or flushing changes until controlled A/B
measurements show an improvement without compromising latency or half-close.
