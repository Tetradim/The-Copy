package org.tetherbridge.classicapp

import android.content.Context
import android.content.pm.PackageManager

object WifiDirectAssist {
    fun isHardwareAvailable(context: Context): Boolean {
        return context.packageManager.hasSystemFeature(PackageManager.FEATURE_WIFI_DIRECT)
    }

    fun guidance(context: Context): String {
        val support = if (isHardwareAvailable(context)) "hardware reports Wi-Fi Direct support" else "hardware support not reported by PackageManager"
        return "Wi-Fi Direct Assist: $support. Use Create Wi-Fi Direct Group to create a separate peer network, then connect Windows and start Wi-Fi Bridge."
    }
}
