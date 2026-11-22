package org.tetherbridge.classicapp

import android.content.Context
import android.net.wifi.WifiManager

object WifiAssist {
    fun bestGuessPhoneHost(context: Context): String {
        val wifiIp = currentWifiIp(context)
        if (!wifiIp.isNullOrBlank() && wifiIp != "0.0.0.0") return wifiIp
        return "192.168.43.1"
    }

    fun currentWifiIp(context: Context): String? {
        val manager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return null
        val ip = manager.connectionInfo?.ipAddress ?: 0
        if (ip == 0) return null
        return listOf(
            ip and 0xff,
            ip shr 8 and 0xff,
            ip shr 16 and 0xff,
            ip shr 24 and 0xff
        ).joinToString(".")
    }

    fun guidance(context: Context): String {
        val guess = bestGuessPhoneHost(context)
        return "Wi-Fi Bridge uses the phone IP + token. Connect the laptop to this phone/hotspot/Wi-Fi network, then pair with host=$guess and port=${ProxyConfig.DEFAULT_PHONE_PORT}. For Wi-Fi Direct, create the group first and use its pairing payload."
    }
}
