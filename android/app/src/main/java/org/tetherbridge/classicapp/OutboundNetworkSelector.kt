package org.tetherbridge.classicapp

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities

class OutboundNetworkSelector(private val context: Context) {
    fun describeActiveNetwork(): String {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val network = cm.activeNetwork ?: return "No active Android network"
        val caps = cm.getNetworkCapabilities(network) ?: return "Active network has no capabilities"
        val types = mutableListOf<String>()
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) types.add("cellular")
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) types.add("wifi")
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_BLUETOOTH)) types.add("bluetooth")
        if (caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) types.add("validated")
        return if (types.isEmpty()) "unknown" else types.joinToString(", ")
    }
}
