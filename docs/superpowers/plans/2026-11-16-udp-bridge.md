# UDP Bridge Implementation Plan

> **For agentic workers:** Implement and verify each slice with focused tests. This is part of the existing TetherBridge development request; the user has authorized implementation.

**Goal:** Carry SOCKS5 UDP datagrams through the existing authenticated phone connection without changing Windows routing or the active PdaNet connection.

**Architecture:** A loopback SOCKS5 UDP association owns a bounded set of destination-specific framed streams. Each stream requests an authenticated UDP tunnel from Android; datagrams are length-prefixed across the existing USB or pinned TLS transport. Association control-channel closure tears down sockets and flows. Full-device routing remains disabled until route/DNS recovery is independently implemented and tested.

**Tech Stack:** .NET 8, Kotlin/Android, SOCKS5, existing framed USB transport and TLS transport.

---

### Task 1: Datagram format and validation

- [x] Add socket-level tests for IPv4, IPv6, domain destinations, malformed packets, and fragmented datagrams.
- [x] Implement strict SOCKS5 UDP packet parsing and serialization; run focused tests.

### Task 2: Phone UDP tunnel

- [x] Add authenticated `CONNECT-UDP` tunnel with bounded datagram framing and connection cleanup.
- [x] Build and test against a local Android UDP echo server without changing system network settings.

### Task 3: SOCKS5 UDP association

- [x] Add bounded destination flows, source endpoint pinning, cancellation on control-channel closure, and UDP reply framing.
- [x] Test using local fake phone endpoints and the connected tablet; verify existing TCP and TLS tests.

### Task 4: Integration and handoff

- [x] Update protocol, compatibility, and maintainer documentation.
- [x] Build both applications and run regression and isolated USB tests. Do not change system routes, DNS, adapters, or PdaNet.
