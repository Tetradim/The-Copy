package org.tetherbridge.classicapp

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Date
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.X509KeyManager
import java.security.Principal
import java.net.Socket
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.security.auth.x500.X500Principal

object WirelessTls {
    private const val ALIAS = "tetherbridge_wireless_tls_v4"
    private val lock = Any()

    private fun store(): KeyStore = synchronized(lock) {
        val keys = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!keys.containsAlias(ALIAS)) {
            val now = System.currentTimeMillis()
            val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA512)
                .setCertificateSubject(X500Principal("CN=TetherBridge"))
                .setCertificateSerialNumber(BigInteger.ONE)
                .setCertificateNotBefore(Date(now - 86400000L))
                .setCertificateNotAfter(Date(now + 315360000000L))
                .build()
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(spec)
                generateKeyPair()
            }
        }
        keys
    }

    fun fingerprint(): String = MessageDigest.getInstance("SHA-256")
        .digest(store().getCertificate(ALIAS).encoded)
        .joinToString("") { "%02X".format(it.toInt() and 0xff) }

    fun serverContext(): SSLContext {
        val managers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
        managers.init(store(), null)
        val delegate = managers.keyManagers.filterIsInstance<X509KeyManager>().first()
        // The Android keystore can contain older keys; always present the exact
        // certificate whose fingerprint is in this pairing payload.
        val selected = object : X509KeyManager {
            override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
            override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String? = null
            override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? =
                if (keyType == "EC") arrayOf(ALIAS) else null
            override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? =
                if (keyType == "EC") ALIAS else null
            override fun getCertificateChain(alias: String?): Array<X509Certificate>? = delegate.getCertificateChain(alias)
            override fun getPrivateKey(alias: String?): PrivateKey? = delegate.getPrivateKey(alias)
        }
        return SSLContext.getInstance("TLS").apply { init(arrayOf(selected), null, null) }
    }
}
