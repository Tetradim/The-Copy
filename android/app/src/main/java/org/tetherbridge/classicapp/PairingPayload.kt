package org.tetherbridge.classicapp

import android.content.Context
import android.net.Uri
import java.util.Locale

object PairingPayload {
    private const val PAYLOAD_SCHEME_EXAMPLE = "tetherbridge://pair"
    // Emits a clean-room tetherbridge://pair?... QR/manual pairing payload.
    fun build(context: Context, mode: BridgeMode = BridgeMode.WIFI, hostOverride: String? = null): String {
        val token = TokenStore.getOrCreateToken(context)
        val host = hostOverride ?: WifiAssist.bestGuessPhoneHost(context)
        return Uri.Builder()
            .scheme("tetherbridge")
            .authority("pair")
            .appendQueryParameter("version", BuildConfig.VERSION_NAME)
            .appendQueryParameter("mode", mode.name.lowercase(Locale.US))
            .appendQueryParameter("host", host)
            .appendQueryParameter("port", ProxyConfig.DEFAULT_PHONE_PORT.toString())
            .appendQueryParameter("token", token)
            .appendQueryParameter("fingerprint", WirelessTls.fingerprint())
            .build()
            .toString()
    }
}
