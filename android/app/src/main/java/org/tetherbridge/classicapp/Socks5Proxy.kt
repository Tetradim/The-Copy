package org.tetherbridge.classicapp

/**
 * Placeholder for v0.4+.
 *
 * Full Laptop Routing is easier if the phone exposes SOCKS5, because tun2socks-style
 * engines commonly speak SOCKS5 and can represent TCP, UDP ASSOCIATE, and authentication
 * more naturally than HTTP CONNECT.
 *
 * Required before enabling production full-tunnel mode:
 * - TCP CONNECT equivalent
 * - UDP ASSOCIATE or custom UDP relay
 * - token auth
 * - connection/byte counters for diagnostics
 * - clear error mapping for DNS/UDP unsupported cases
 */
class Socks5Proxy
