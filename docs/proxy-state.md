# Reversible Windows proxy state

The companion must never enable the Windows proxy without saving the user's previous configuration first.

## Save

Save the following to a timestamped JSON profile:

- WinINet current-user proxy enabled flag
- proxy server string
- proxy bypass list
- auto-config URL
- auto-detect flag
- WinHTTP proxy state, if the user chooses to sync WinHTTP
- app version that created the state
- active mode that created the state

## Enable

Set WinINet current-user proxy to the local relay only:

```text
HTTP=127.0.0.1:10809;HTTPS=127.0.0.1:10809
```

Do not store phone IPs or tokens in global settings.

## Disable

Restore the saved state exactly.
If no saved state exists, offer a safe reset that disables proxy and clears only TetherBridge-owned values.

## Crash recovery

On app start:

1. Detect if proxy currently points to `127.0.0.1:10809`.
2. Check whether the local relay is running.
3. If not running, show `WININET_PROXY_STALE` with a one-click restore.
