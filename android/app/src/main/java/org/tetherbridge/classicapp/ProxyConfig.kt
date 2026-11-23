package org.tetherbridge.classicapp

import java.security.SecureRandom
import android.util.Base64

enum class BridgeMode { USB, WIFI, BLUETOOTH_PAN, BLUETOOTH_RFCOMM }

data class ProxyConfig(
    val mode: BridgeMode,
    val listenHost: String,
    val listenPort: Int,
    val token: String,
    val requireToken: Boolean
) {
    internal fun canReuseUsb(request: ProxyConfig, backendListening: Boolean, framesListening: Boolean): Boolean =
        mode == BridgeMode.USB && request.mode == BridgeMode.USB && this == request && backendListening && framesListening

    companion object {
        const val DEFAULT_PHONE_PORT: Int = 10888

        fun forMode(mode: BridgeMode, token: String = generateToken()): ProxyConfig {
            return when (mode) {
                BridgeMode.USB -> ProxyConfig(
                    mode = mode,
                    listenHost = "127.0.0.1",
                    listenPort = 0,
                    token = token,
                    requireToken = false
                )
                BridgeMode.BLUETOOTH_RFCOMM -> ProxyConfig(
                    mode = mode,
                    listenHost = "127.0.0.1",
                    listenPort = 10892,
                    token = token,
                    requireToken = true
                )
                BridgeMode.WIFI, BridgeMode.BLUETOOTH_PAN -> ProxyConfig(
                    mode = mode,
                    listenHost = "0.0.0.0",
                    listenPort = DEFAULT_PHONE_PORT,
                    token = token,
                    requireToken = true
                )
            }
        }

        fun generateToken(): String {
            val bytes = ByteArray(18)
            SecureRandom().nextBytes(bytes)
            return Base64.encodeToString(bytes, Base64.NO_WRAP or Base64.URL_SAFE)
        }
    }
}
