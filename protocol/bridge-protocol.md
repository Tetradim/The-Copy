# Bridge protocol

**Current wireless transport:** Wi-Fi/PAN requires TLS 1.2 or later and a pinned SHA-256 certificate fingerprint from the pairing URI. The historical plaintext and v0.6.1 pairing examples below are obsolete and must not be used with current builds. USB 0.6.4 uses a framed loopback transport over ADB on Android port 10889; Android 0.6.5 and later use an internal loopback HTTP proxy on port 10890 for USB and port 10888 for Wi-Fi/PAN. The frame format is a big-endian 32-bit length followed by up to 32768 data bytes; zero length signals write EOF.

## Ports

| Component | Default bind | Purpose |
|---|---|---|
| Android bridge | `127.0.0.1:10889` in USB mode | Framed endpoint via ADB forward; internal HTTP proxy is loopback port 10890 |
| Android bridge | `0.0.0.0:10888` in Wi-Fi/PAN mode | Phone proxy endpoint over local network |
| Windows local relay | `127.0.0.1:10809` | Stable Windows system proxy endpoint |

## Authentication

The Windows relay adds one of the following to requests forwarded to the phone:

```http
X-TetherBridge-Token: <pairing-token>
```

or

```http
Proxy-Authorization: Basic base64("tb:<pairing-token>")
```

The Windows system proxy settings never include the token.

## Health endpoints

### `GET /health`

No token required in USB loopback mode; token required for Wi-Fi/PAN mode.

Response:

```json
{"ok":true,"app":"TetherBridge","version":"0.4","mode":"wifi","proxyPort":10888}
```

### `GET /status`

Token required.

Response:

```json
{
  "ok": true,
  "mode": "wifi",
  "listenAddress": "0.0.0.0",
  "proxyPort": 10888,
  "bytesUp": 1234,
  "bytesDown": 5678,
  "activeConnections": 2
}
```

## Proxy support

### HTTP CONNECT

Required for HTTPS traffic:

```http
CONNECT example.com:443 HTTP/1.1
Host: example.com:443
X-TetherBridge-Token: ...
```

### Absolute-form HTTP

Required for plain HTTP through system proxy:

```http
GET http://example.com/path HTTP/1.1
Host: example.com
X-TetherBridge-Token: ...
```

### SOCKS5

Recommended for Full Laptop Routing because tun2socks integrations usually prefer SOCKS5 and can handle more cases than HTTP CONNECT. Implement after v1 proxy stability.


## v0.6.1 pairing payload

Android displays a QR/manual payload in this format:

```text
tetherbridge://pair?host=<phone-ip>&port=10888&mode=wifi&token=<pairing-token>
```

Windows parses this payload in the Pairing tab and fills the Wi-Fi Bridge host/token fields. The token must be redacted from exports and logs.

## v0.6.1 status response

`GET /status` returns JSON with `version`, `mode`, `listenHost`, `proxyPort`, `uptimeMs`, `timestampMs`, and counters such as `activeConnections`, `totalConnections`, `bytesUp`, and `bytesDown`.


## Pairing payload

v0.6.1 adds a URI payload intended for QR display and copy/paste pairing:

```text
tetherbridge://pair?version=0.6.1&mode=wifi&host=<phone-ip>&port=10888&token=<pairing-token>
```

Windows treats the payload as convenience input only. The real security boundary remains the Android token check plus per-peer auth backoff.

## UDP tunnel in Android 0.6.5 / Windows 0.6.6

`CONNECT-UDP host:port HTTP/1.1` authenticates like the existing CONNECT request. After a 200 response, each direction carries big-endian 32-bit datagram lengths followed by that many bytes; length zero is an empty UDP datagram. Lengths above 65507 are invalid. The Windows local SOCKS5 relay handles UDP ASSOCIATE for loopback clients and sends each destination through a dedicated authenticated tunnel. SOCKS fragmentation is not supported. TCP control-channel closure closes the association and its destination flows. UDP is only enabled when Android advertises the `udp` capability in status JSON.
