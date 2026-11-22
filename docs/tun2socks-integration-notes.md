# tun2socks integration notes

The full-routing prototype should treat tun2socks as a replaceable engine behind `FullTunnelService`.

## Minimal command-shape target

The service should eventually produce a command equivalent to:

```powershell
tun2socks --device wintun --proxy socks5://127.0.0.1:10810 --interface "<primary-interface>"
```

The exact arguments depend on the selected tun2socks build and should be pinned in release notes.

## Required surrounding work

`tun2socks` alone is not the product. TetherBridge still has to:

- create/open the adapter,
- assign adapter IP/DNS,
- protect the phone-transport route from being captured by the tunnel,
- route default traffic into the adapter,
- capture/restore DNS and route state,
- expose logs and stats in the Windows UI,
- stop safely if the phone bridge dies.

## Local relay requirement

Before v0.7, the local relay should grow a SOCKS5 listener or a tun2socks-compatible proxy endpoint. HTTP CONNECT alone is not enough for a polished full-routing mode, especially for DNS and UDP.
