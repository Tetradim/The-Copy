# Tether Bridge parity progress and completion gate

Last reviewed: 2026-11-17. This is a monitoring record, not a claim of completion. The user requests all eight PdaNet desktop parity areas and a helper that logs omissions and flags premature stopping. Keep PdaNet's active connection available; tell the user before any operation that could interrupt it.

| Requirement | Evidence and current status | Completion evidence still needed |
|---|---|---|
| 1. Full-computer routing | **Missing.** FullTunnel code contains contracts and plans, not adapter creation or route management. Existing WinINet proxy covers only cooperating applications. | Working virtual adapter and TCP/UDP path; IPv4/IPv6 and VPN/transport exclusions; elevated service; transactional route management; successful actual non-proxy application test. |
| 2. System DNS handling | **Partial.** DNS validation, local UDP/TCP listener and SOCKS UDP resolver exist; desktop has opt-in loopback DNS controls. Code/source testing is not the same as Windows system DNS integration. | Timeout/error/security checks, appropriate DNS policy, interface configuration and crash-safe restoration under full routing. Do not silently alter DNS for the current PdaNet connection. |
| 3. Wi-Fi Direct | **Partial, unverified on device.** Android source now contains explicit group creation/removal and credential display; unit policy tests and build passed per lead agent. Windows still uses ordinary Wi-Fi/IP pairing. Lifecycle and ownership issues remain under review. | Resolve ownership and asynchronous lifecycle races; verify group creation, credentials/discovery, permissions, client connection, secure pairing, lifecycle and real-device tests. Physical network test may affect connectivity; tell the user before any such operation. |
| 4. Bluetooth connection | **Partial, untested on device.** Android now has an RFCOMM listener forwarding to its token-protected TLS loopback proxy; build and configuration test passed per lead agent. Windows paired-device enumeration and RFCOMM client with TLS pinning are implemented. Lead agent reports isolated Samsung paired RFCOMM health/status probes passed without changing Windows proxy/routes/DNS and with the PdaNet ADB forward preserved. | End-to-end TCP/UDP data, half-close, failure/recovery, sustained load, and OnePlus testing remain. |
| 5. Wi-Fi Share | **Missing.** No implementation that shares the Windows bridge to other devices. | Working Windows sharing/hotspot integration, supported interface checks, cleanup, authorization and multi-client tests. |
| 6. Desktop usability and recovery | **Partial.** Device selection, USB/TLS proxy, forward ownership and proxy snapshots exist; recovery and safety remain incomplete. | Installer and dependency handling, tray workflow, explicit diagnostics, ownership-aware restoration, sleep/resume, device disconnect, crash/reboot and failure-injection tests. |
| 7. Device and application testing | **Partial.** Lead agent reports latest Samsung Android 0.6.6 USB tests passed framed HTTP HTTPS, client half-close, SOCKS HTTPS, UDP DNS and TCP DNS via a temporary owned ADB forward; PdaNet tcp:8739 remained intact. Physical Wi-Fi/PAN and OnePlus not tested; no full app-routing test. | Real OnePlus and Samsung, physical Wi-Fi/Bluetooth, simultaneous devices, browser and non-proxy apps, TCP/UDP/DNS, sustained load and reconnection. Preserve PdaNet while testing. |
| 8. Other PdaNet options | **Partial.** A read-only desktop label inventory and comparison is documented in `pdanet-desktop-comparison.md`; block Microsoft connections and Hide Tether Usage remain incomplete; opt-in per-direction session bandwidth pacing is implemented but not end-to-end validated; opt-in USB plug-in auto-connect was added but is not yet live-tested. | Verify cited public help and safely observable option behavior; document exact scope and unsupported behaviors, implement/test applicable options, and do not claim equivalence from strings alone. |

## Monitoring rules

- Do not mark a requirement complete from a scaffold, successful build, simulated test, or a single happy-path connection. Identify the relevant implementation and its actual end-to-end evidence.
- User explicitly rejected stopping after a single slice. Continue independent implementation and testing until the requested scope is genuinely complete, unless a real blocker requires user action; do not equate output length or a partial milestone with completion.
- Do not change PdaNet, Windows routes, DNS, adapters, or proxy settings in a way that can interrupt the user's current Internet connection without first telling the user. Isolated loopback tests and simulated state do not require taking that connection down.
- Current user-owned source is `the repository root`. Existing release assets are test/debug builds. Keep this status accurate when behavior changes; do not imply that open issues are complete.

## DNS review, 2026-11-17

- The desktop TCP listener now uses DNS over TCP via SOCKS CONNECT. The lead agent reports the Samsung USB live-device DNS test passed for both UDP and TCP with successful response codes, nonzero answer counts and no truncation. This establishes those exercised paths, not Windows system DNS integration.
- The lead agent reports the repeated Samsung live-device UDP/TCP probe passed after adding answer-record framing and IN/A validation; malformed/truncated record regression also passed. A simulated listener regression covers a truncated UDP response followed by a client TCP retry using the separate resolver; it does not exercise the SOCKS or physical-device retry paths. Failure/recovery and Windows system integration remain outstanding.

## Desktop safety review

- Restoration now requires the exact bridge-applied proxy fields and preserves the current AutoDetect value; endpoint matching no longer uses substrings. The lead agent reports passing tests. The lead agent added mixed-state checks before relay activation and blocks disconnect/close if Windows still references the relay. Connection-change preparation now checks mixed references before stopping and checks again afterward, closing the identified stable-state bypass. StopExistingAsync now checks for a live reference before cleanup, including after the awaited health probe. External settings can still change after the check or between ownership check and registry writes; these are residual races. Test mixed-state transitions and state changes during awaited operations. Forced termination and system shutdown still require crash recovery.

## Full-routing safety implementation review

- The full-tunnel directory contains interfaces and textual plans only; it does not implement a tunnel, privileged service, adapter, route changes, or crash recovery. Some full-tunnel design text predates the SOCKS UDP and local DNS work.
- A safe testable next step is an injected transaction coordinator with durable write-ahead recovery records. Record per-operation ownership and original values, reconcile current values before restoring, and preserve unrelated external changes. Secure the elevated control channel and restrict its accepted operations.
- Test every partial-start failure, reverse rollback, repeated stop/recovery, journal corruption and torn writes, process/service crash, external configuration changes, endpoint exclusions (USB ADB versus Wi-Fi/PAN), IPv4/IPv6 policy, DNS, and child-process failure with fakes first. Device-backed route changes remain untested and must follow the user's requirement to be told before risking the active PdaNet connection.

## Wi-Fi Direct review

- New group controller remains untested on physical devices. It now uses a longer random SSID plus a stored credential digest, and rejects an existing group even if its name is unavailable. Identity is checked before removal, but the group can change between check and removal.
- A generation-tagged process-wide operation gate, timeout and asynchronous exception handling now avoid indefinite busy states and stale callbacks releasing newer operations. Persisted create_pending now blocks a second creation after timeout until the owned group is observed, closing the identity-overwrite case. However, process death before createGroup or a late failure ignored after timeout can leave creation blocked indefinitely. Timed-out removals lack a persisted pending marker and need reconciliation before permitting a new group. Test delayed platform operations, explicit recovery, recreation and permission failures.

## Transaction coordinator review

- A backend-injected write-ahead coordinator and fake-backend regression now exist; they do not implement actual adapters or routing. Recovery retains conflicts and reverses successful operations, but security and crash durability are not yet established.
- An exclusive lock file now covers Apply and Recover for cooperating same-path instances, and recovery stops at the first conflict; the lead agent reports independent-instance and conflict-barrier tests pass. Elevated use still requires protected parent/lock/journal/temp paths, anti-reparse handling, bounded and strictly validated operations, and a concrete backend with crash-idempotent expected-state checks.
- Test dependency-aware handling when a later rollback conflicts, torn/partial journal writes, crashes at every write/mutation boundary, independent user changes, concurrent starts and retries. A file flush and rename alone are not proof of full power-loss durability.

- The lead agent reports downloading and hash-checking Wintun 0.14.1 and tun2socks 2.7.0 archives. No driver installation or routing change was made; provenance, licenses, integration and live recovery remain to be verified.

## Dependency staging review

- The staging script pins archive hashes before extraction and checks the Wintun DLL signature. Independent inspection found the staged Wintun signature valid and signed by WireGuard LLC. Temporary archives had already been deleted, so the staged bytes were not independently compared to archive members in this review.
- Script writes individual destination files before downloading the tun2socks license; failures can leave mixed or partial output. Stage and validate the complete set before publishing, and pin the separately fetched license. Record applicable transitive license notices.
- An elevated service must not execute or load binaries from a user-writable staging folder. Install to an administrator-protected location and verify paths, ACLs, binary identity and DLL search behavior at point of use. Trusted provenance of pinned hash values and production packaging remain unverified.

## Proxy startup recovery review

- Startup recovery is now read-only, historical snapshots require an active marker, failed restoration does not disable the proxy, and the user mutex uses the Global namespace. Tests pass per lead agent; no live registry changes were made during review.
- A snapshot marker is written before applying the proxy, so crash or external changes can leave a stale marker. Startup no longer restores automatically; however the Restore action still applies the snapshot without showing its values or identifying an unrelated listener. Provide an informed review and test crash-before-apply and later user proxy changes.
- The Global user mutex improves cross-session coordination. Registry ownership/read-write races remain, and no actual crash/reboot test has been performed. Close/stop logic can block when an external proxy references the same port even if this process never owned a relay; distinguish actual relay ownership.

## Bluetooth RFCOMM review

- Android source uses authenticated RFCOMM and forwards to the token-protected TLS listener. Fatal accept errors now report to the foreground service; test adapter-off, permission-revocation and recovery failures.
- Lead agent reports isolated Samsung health/status probes passed over paired Windows RFCOMM with TLS pinning and token authentication, with PdaNet forward preserved. Android now closes both sockets at target EOF and Windows probes honor HTTP framing. Test whether full socket close preserves needed reverse-direction half-close semantics; TCP/UDP forwarding, abrupt EOF, connection limits, recovery and sustained load remain unverified.

## Samsung Bluetooth data observations

- Lead agent reports isolated paired RFCOMM tests rejected a bad certificate and token, and HTTPS via the local HTTP proxy passed after the WinRT stream flush fix. First UDP attempt timed out after HTTPS; a separate repeated run passed SOCKS UDP DNS and TCP DNS. These are partial real-device results, not a reliability pass.
- The sequential-session timeout remains undiagnosed; collect correlated timing and resource/EOF evidence across Windows and Android, then repeat HTTPS-to-UDP-to-TCP sequences and test half-close/recovery. PdaNet forward remained present; Windows proxy, routes and DNS were not modified during those tests.

- Lead agent reports live Samsung RFCOMM half-close passed with framed records inside pinned TLS against an Android-local netcat server. An earlier ADB-reverse echo returned empty because that test path did not preserve half-close and is not evidence of a Bluetooth failure. Tests preserved PdaNet settings.
- Review still needs idle pre-auth/TLS timeouts, malformed and truncated frames, persistent accept failure behavior, simultaneous half-closes and repeated sequential data tests; the earlier intermittent UDP timeout is not yet explained.

- Lead agent reproduced Windows RFCOMM address-in-use for concurrent connections on a single channel. Android now registers eight services and Windows leases slots; two simultaneous pinned-TLS streams passed on Samsung. A separate earlier sequential UDP timeout remains to be rechecked under load.
- Remaining review findings: eight occupied data slots can starve health probes and trigger false disconnect; older single-service Android builds need version negotiation or a clear compatibility error; lease release must survive all disposal exceptions. Validate channel availability across devices, unauthenticated idle limits and eight-slot concurrency.

- Slots 6 and 7 are now reserved for health and status and disposal always releases a lease. Lead agent reports a live Samsung health probe passed while six data streams were held. Android adds a 15-second pre-authentication timer and framed-listener fatal reporting. Deadline cancellation now uses an explicit backend token-validation callback keyed to loopback source port. Lead agent reports live Samsung unauthenticated timeout and authenticated CONNECT lasting over 20 seconds passed. Per-service-generation registry and synchronized auth/expiry now address restart callbacks and the boundary race; unit tests/build pass per lead agent. Investigate delayed same-generation callback after source-port reuse, and test stalled peers and other device channel limits.

## Tray and auto-connect review

- Tray connect/disconnect/exit and opt-in USB auto-connect were added; no live app or network settings were changed during review. A shared action gate now serializes connect, disconnect, reconnect and close; a fake delayed-connect/close test passes per lead agent. Reentrant close events now cancel until a separate approval flag is set after gated cleanup, closing the identified bypass. Test repeated close during delayed work; close waits rather than cancels, so device operations need bounded waits. Forced termination and OS shutdown still require crash recovery.
- Hidden tray failures are logged to the hidden form; show actionable errors to the user. The local DNS ephemeral-port retry cleans up failed listeners and does not itself change Windows DNS. Real desktop lifecycle and crash tests remain outstanding.

## Windows installer review

- A self-contained v0.6.8 installer package was built but not installed. The scripts do not intentionally change PdaNet, Windows proxy, routes, DNS, or drivers. v5 checks shortcut ownership and uninstalls hash-matching owned files while preserving settings; the lead agent reports isolated Windows PowerShell install, retry and uninstall tests passed.
- Unknown process paths now fail closed, retry compares manifests, and shortcut paths are checked for reparse points with exact-version ownership. Failed-staging cleanup remains open. Hashes in an unsigned adjacent manifest detect accidental changes but do not authenticate a package; verify provenance and process/start races before release.

## USB hot-plug review

- Opt-in authorized-device hot-plug detection, baseline, and serialized action were added; unit tests/build passed per lead agent, with no live app/network test. Hotplug now rechecks opt-in, selected device, authorization and proxy state inside the gate and checks eligibility through setup. Startup and hotplug now bind the captured ADB serial and recheck eligibility through setup. Test delayed gate, exit, opt-out, device substitution and transient ADB enumeration on devices.

## Bandwidth pacing review

- Opt-in session-wide per-direction pacing is implemented with unlimited default; unit tests/build pass per lead agent. Low rates can hold a shared direction gate for a large write and starve other streams or exceed DNS timeouts; verify fairness and end-to-end TCP/UDP/DNS behavior. Cancellation after reading but during the pacing delay can consume bytes without returning them; define safe stream-cancellation semantics and test.

## Expiring tunnel-route design review

- The lead agent added command-level route builders, with no route changes. Microsoft documents netsh route lifetime and active-store parameters; local IPv4 help confirms command shape. Actual expiry/renewal must be verified in an isolated Windows environment before relying on it.
- Split /1 routes do not replace more-specific existing routes and need protected transport endpoint routes; hardcoded gateways must be reachable through a correctly configured tunnel adapter. Expiration does not restore DNS/proxy/adapter state and can fall back to another interface. Validate leases, partial failure rollback, interface identity, sleep/reboot and external conflicts before any real use.

## Latest device regression evidence

- Lead agent reports Samsung Android 0.6.6 USB framed HTTPS, client half-close, SOCKS HTTPS, UDP DNS and TCP DNS passed using a temporary owned ADB forward; PdaNet tcp:8739 remained intact. Bluetooth bandwidth-limited HTTPS and UDP DNS also passed. These checks do not establish full-device routing, Wi-Fi Direct, multi-device reliability or OnePlus support.

## Route lease manager review

- An allowlisted netsh runner and simulated reverse-unwind route lease manager exist but are not invoked; no routes changed. The lead agent reports fake partial-activation and command tests passed.
- Manager now serializes operations and requires an inspector checking adapter GUID/LUID and route identity before add/renew/delete; lead agent reports preexisting/conflict and partial-mutation fake tests pass. No real inspector exists. Identical routes cannot be attributed atomically across inspect/netsh; require exclusive private adapter ownership and test race handling. In-memory state, partial renewal, failed rollback visibility, DNS/endpoint exclusions and finite-lifetime behavior remain to be addressed and tested in an isolated VM.

## Native route inventory review

- Lead agent reports read-only native route enumeration matched a Windows default route and fake inspector tests passed; no routes changed. This does not verify full native ABI layout across architectures or IPv6 rows. Cross-check field offsets, multi-row IPv4/IPv6 enumeration and address scope in an isolated environment.
- Inspector checks GUID/LUID/index, gateway, metric and non-immortal lifetime, but prepared identity cannot prove transaction ownership across concurrent changes. The manager currently uses one index for both protocol families even though Windows may report different indices. Validate distinct indices, gateway reachability and actual lease bounds/expiry before integration.

## Tunnel engine launcher review

- Fixed-path hash-pinned tun2socks/Wintun launcher, private random adapter name and kill-on-close job exist but have not been invoked; argument and job-kill unit tests passed per lead agent. No adapter or route was created.
- Program Files naming alone does not prove secure ACLs, file reparse safety or safe DLL search. Process.Start before assignment to the job has a crash window that can orphan the engine; use suspended creation/job assignment before resume or equivalent. Verify child exit and adapter cleanup rather than assuming job close removes it, and test hostile replacement, failure/timeout, process death and actual ownership in isolation.

- Launcher now uses creation-time job assignment, restricted PATH, final-directory inherit-only ACL checks and unexpected sibling DLL rejection. Lead agent reports adversarial ACL and actual helper/job termination tests pass. PATH alone does not prove the entire DLL search path is safe; test actual engine loading, ownership and adapter cleanup in isolation. Route ownership remains non-atomic. No driver-backed launch or network change was performed by this review.

## Local DNS failure handling

- Lead agent reports UDP/TCP SERVFAIL regression passed; no Windows DNS settings changed. Generated failures preserve ID/question and remove additional sections. RR framing and owner-name compression are now validated and asynchronous noncooperative resolver tasks are bounded by WaitAsync while retaining permits; lead agent reports regressions pass. Remaining limits: synchronous delegate blocking bypasses WaitAsync, RDATA semantic validity is not fully checked, and enough never-ending tasks permanently saturate permits and cause dropped requests. Test and document saturation and TCP truncation policy.

- Dependency preparation now pins binaries and both licenses before same-parent publication; the elevated installer has only been exercised with WhatIf. Installer now checks trusted root ACL and reparse paths, pins both licenses, uses protected staging, verifies copied files and publishes atomically. Lead agent reports WhatIf and isolated Windows PowerShell 5 protected-directory creation tests passed. A real end-to-end elevated install and injected-failure/cleanup tests remain unverified; no driver activation or networking change occurred.

- Adapter command builders now exist but are not invoked. Current address is identical to the proposed route next hop for each family; validate a real tun2socks on-link/peer design. DNS command targets loopback port 53 while the existing local DNS service defaults to 1053, and DNS configuration persistence/restoration is unverified. Do not activate without isolated end-to-end routing/DNS and crash-recovery tests.

- Lead agent reports pinned tunnel dependencies were installed into protected Program Files and TunnelPathTrust verified actual installed ACLs/files. This was dependency installation only; no driver, adapter, routes or DNS were activated.
- The uninvoked adapter configurator now checks other route-prefix overlap, adapter identity and sole loopback DNS; DNS listener uses exclusive sockets. These snapshots remain non-atomic, and exclusive binding does not prove healthy resolution or all Windows DNS policies. The proposed next hops match local adapter addresses, failure teardown is delegated to a future caller, and isolated end-to-end routing/DNS/recovery testing remains required.

- Lead agent reports local dual-stack UDP/TCP DNS tests and a Samsung USB query via IPv6 loopback passed, without Windows DNS or route changes. Private-adapter IPv6 DNS commands and a route-bypass classifier are uninvoked. The classifier deliberately preserves LAN/private routes, which may include VPN or carrier traffic; equal-prefix metrics and Windows NRPT/DoH/multi-interface DNS behavior still require real system testing. Do not claim leak protection or full traffic coverage from the classifier.

## Tunnel supervisor review

- A serialized lifecycle supervisor with fake runtime tests now exists; it retains engine state if route removal remains unresolved. It is not connected to a real full-routing runtime. Automatic lease renewal, phone/engine/DNS health monitoring, durable crash recovery and actual cleanup remain outstanding.
- Cleanup after failure uses an uncancelled token and can hang if a runtime operation never returns. Define bounded cleanup and test hangs, cancellation, sleep, process/engine death, partial renewal and concurrent stop. Preflight must remain side-effect-free because failures there do not enter cleanup.

## November 18 lease monitoring and Samsung DNS check

- Added a lease monitor with periodic health checks, renewal deadlines, and route-before-engine cleanup on failed health or cancellation. Fake-runtime tests cover health failure, renewal, cancellation, and a synchronously blocked health check. It is not connected to a Windows runtime or UI and does not implement crash persistence. A stalled mutating renewal or cleanup still needs a safe bounded recovery design.
- The existing Samsung USB bridge passed isolated UDP and TCP DNS resolution through a temporary ADB forward. The temporary forward was removed; the pre-existing PdaNet forward remained. No system DNS, routes, hotspot, or proxy settings were changed. The OnePlus was not connected over ADB.
- Upstream tun2socks Windows documentation shows the interface address also used as its route next hop, but the project's route/IPv6 configuration and lease expiry still require live Windows validation; this documentation alone does not establish that the current commands work.

## November 19 routing recovery corrections

- Route lease management now records a conservative timestamp before the first route mutation and checks that age during sequential renewal. Simulated slow activation and renewal tests require cleanup before a stale lease is reported active. Actual Windows lifetime and sleep behavior remain unverified.
- Network transaction activation now explicitly reports unresolved recovery after a conflict and preserves the original error; recovery rejects duplicate journal identities before touching backend state. Fake-backend tests cover conflict, retry and invalid journal cases. Journal path security and actual crash durability are still unresolved.
- Desktop relay tests, desktop build, repository regression checks and Android unit test/build passed. The Samsung was the only ADB-connected device, and the PdaNet tcp:8739 forward remained present. No routing, system DNS or sharing changes were made.

- Added an opt-in Wi-Fi Share path to the Windows tunnel lab. It requires a Windows connection profile for the exact tunnel adapter, checks hotspot capability and idle state, then uses Windows Mobile Hotspot with the saved configuration. Startup and teardown are serialized while route renewal runs. The Release build, CLI checks and repository regressions passed. Hotspot activation, Wintun profile compatibility, client traffic and crash recovery remain unverified. The PdaNet forward was preserved.

- The tunnel DNS preflight now requires a complete IPv4 address answer instead of accepting any nonempty answer section. The local preflight, relay suite, Release build and regression checks passed.
- Windows packaging now stages and hashes the payload before publishing the installer directory in one rename. Fixture checks passed for hidden files, matching hashes, existing output and output nested under the source. The retained installer was left in place.
- Installer now rejects alternate data streams, Windows device names and ambiguous path components before writing the payload. Installation commits with a non-overwriting directory rename. Isolated path rejection, install/uninstall and repository checks passed. Failed installation staging cleanup remains open.
- Desktop USB, Wi-Fi and Bluetooth now probe candidate bridge health before teardown of a working session. A rejected USB candidate releases its own forward, invalid Wi-Fi ports are rejected early, and Bluetooth PAN diagnostics no longer disconnect. Release build, relay tests and regressions passed. Live desktop transitions remain untested.
- Partial desktop relay startup now rolls back if the second listener or proxy setup fails. HTTP uses an exclusive loopback bind. An isolated occupied SOCKS port test proved rollback of the first listener, and the Release build and regression checks passed.
- Windows packaging now uses the executable product version. Upgrades can replace a shortcut only when it targets a verified older installation under the chosen root. Isolated version detection, upgrade, downgrade rejection, uninstall and regressions passed. Prior versions remain separately uninstallable.
- Packaged desktop 0.6.9 as dist/TetherBridge-0.6.9-win-x64.zip (78,837,430 bytes). The archive was extracted and the installer verified through isolated install and uninstall. Removed the staging copies from the workspace. Full routing and Wi-Fi Share remain lab-only and require live validation.
- Packaged Android 0.6.7 for device testing. Version display, pairing payload, USB pairing metadata and status use the build version. Android unit tests and assembly passed, and the APK signature and manifest version were checked. The APK was not installed during this run.
- Tunnel Lab now checks the interface Windows actually prefers for addresses in each leased route range after startup and renewal. The IPv4 and IPv6 lookup matched Find-NetRoute on the current system without sending traffic or changing routes. The Release build and regression checks passed. This check still needs a driver-backed test.
- Added a lab HTTPS acceptance check that verifies Windows route selection, binds the request to the exact tunnel adapter address, and disables Windows proxies while retaining TLS validation. Local checks rejected plaintext and an adapter without the tunnel address. Release build and regressions passed. A successful hardware-backed HTTPS check remains pending.
