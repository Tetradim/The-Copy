# Review merge notes

The code review correctly identified three issues to avoid:

1. A body-preservation fix is incomplete if the correctly positioned Android input stream is not the stream later pumped to the remote server.
2. Failed-auth backoff should be per peer and should not sleep while holding a shared lock.
3. UI should not expose Wi-Fi Direct or Bluetooth Socket buttons until those modes have real transport implementations.

v0.6 addresses those points as follows:

- `HttpConnectProxy.forwardHttp()` accepts `clientInput` and passes it to `pipeBoth(clientInput, ...)` after first forwarding `headerRead.tail`.
- `AuthFailureBackoff.kt` tracks counters per source address using `ConcurrentHashMap` and `AtomicInteger`.
- `MainActivity` exposes only real modes: USB Bridge, Wi-Fi Bridge, Bluetooth/PAN Assist, Android tethering settings, token rotation, and Stop Bridge.
- `ProxyConfig` contains only `USB`, `WIFI`, and `BLUETOOTH_PAN`.
- `scripts/run-regression-tests.ps1` now checks these specific guardrails.
