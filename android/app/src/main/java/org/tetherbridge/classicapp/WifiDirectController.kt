package org.tetherbridge.classicapp

import android.content.Context
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pGroup
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.net.Inet4Address
import java.net.NetworkInterface
import java.security.SecureRandom

// Explicit user action only. Never deliberately replace or remove a different P2P group.
class WifiDirectController(private val context: Context, private val report: (String, String?) -> Unit) {
    companion object {
        private val operations = WifiDirectOperationTracker()
    }
    private val manager = context.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
    private val handler = Handler(Looper.getMainLooper())
    private val channel = manager?.initialize(context.applicationContext, Looper.getMainLooper()) {
        // The operation timeout releases any waiter; no network settings are changed here.
        tell("Wi-Fi Direct service disconnected. Refresh group status to check the outcome.", null)
    }
    private val prefs = context.getSharedPreferences("wifi_direct_owner", Context.MODE_PRIVATE)
    private var disposed = false
    private fun tell(message: String, host: String?) { if (!disposed) report(message, host) }
    private fun start(changesGroup: Boolean = true): Long {
        val id = operations.begin(changesGroup)
        if (id == 0L) { tell("An Android Wi-Fi Direct operation is still pending.", null); return 0 }
        handler.postDelayed({ if (operations.finish(id)) tell("Wi-Fi Direct operation timed out. Refresh group status before retrying.", null) }, 20000)
        return id
    }
    private fun complete(id: Long, message: String, host: String? = null) { if (operations.finish(id)) tell(message, host) }
    private fun forgetIfOwned(name: String) {
        if (prefs.getString("group_name", null) == name)
            prefs.edit().remove("group_name").remove("group_digest").remove("create_pending").apply()
    }
    private fun matches(group: WifiP2pGroup?, name: String?): Boolean = group != null && group.isGroupOwner &&
        WifiDirectGroupPolicy.mayRemove(name, group.networkName) &&
        WifiDirectGroupPolicy.credentialsMatch(prefs.getString("group_digest", null), group.networkName, group.passphrase)

    fun create() {
        if (Build.VERSION.SDK_INT < 29) { tell("Wi-Fi Direct group creation requires Android 10 or newer.", null); return }
        val m = manager; val c = channel
        if (m == null || c == null) { tell("Wi-Fi Direct is not available on this device.", null); return }
        if (!WifiDirectGroupPolicy.mayCreateWhenPending(prefs.getBoolean("create_pending", false))) {
            tell("A previous group creation is unresolved. Refresh group status and inspect Android Wi-Fi Direct settings before creating another group.", null); return
        }
        val id = start(); if (id == 0L) return
        try {
            m.requestGroupInfo(c) { existing ->
                if (!operations.isCurrent(id)) return@requestGroupInfo
                try {
                    if (existing != null) { complete(id, "A Wi-Fi Direct group already exists. Leave it intact; stop it in Android settings before creating a new group."); return@requestGroupInfo }
                    val alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
                    val random = SecureRandom()
                    fun randomText(n: Int) = (0 until n).map { alphabet[random.nextInt(alphabet.length)] }.joinToString("")
                    val name = "DIRECT-${randomText(2)}-TB-${randomText(16)}"
                    val passphrase = randomText(16)
                    val config = WifiP2pConfig.Builder().setNetworkName(name).setPassphrase(passphrase).build()
                    if (!prefs.edit().putString("group_name", name).putString("group_digest", WifiDirectGroupPolicy.credentialDigest(name, passphrase)).putBoolean("create_pending", true).commit()) {
                        complete(id, "Could not save group ownership; no group was created."); return@requestGroupInfo
                    }
                    try {
                        operations.submitMutation(id)
                        m.createGroup(c, config, object : WifiP2pManager.ActionListener {
                            override fun onSuccess() {
                                if (operations.settleMutation(id) && operations.isCurrent(id)) waitForGroup(id, name, 0)
                            }
                            override fun onFailure(reason: Int) {
                                if (!operations.settleMutation(id)) return
                                forgetIfOwned(name)
                                complete(id, "Wi-Fi Direct group creation failed (Android code $reason).")
                            }
                        })
                    } catch (ex: Exception) {
                        operations.settleMutation(id)
                        forgetIfOwned(name)
                        complete(id, "Wi-Fi Direct group could not start: ${ex.message}")
                    }
                } catch (ex: Exception) { complete(id, "Could not prepare Wi-Fi Direct: ${ex.message}") }
            }
        } catch (ex: Exception) { complete(id, "Wi-Fi Direct unavailable: ${ex.message}") }
    }

    fun refresh() {
        val name = prefs.getString("group_name", null)
        if (name == null) { tell("No TetherBridge-owned Wi-Fi Direct group is recorded.", null); return }
        val id = start(changesGroup = false); if (id != 0L) waitForGroup(id, name, 0)
    }

    private fun waitForGroup(id: Long, name: String, attempt: Int) {
        if (!operations.isCurrent(id)) return
        val m = manager; val c = channel
        if (m == null || c == null) { complete(id, "Wi-Fi Direct is not available on this device."); return }
        try {
            m.requestGroupInfo(c) { group ->
                if (!operations.isCurrent(id)) return@requestGroupInfo
                try {
                    if (matches(group, name)) {
                        val ip = try {
                            NetworkInterface.getByName(group!!.`interface`)?.inetAddresses?.toList()
                                ?.filterIsInstance<Inet4Address>()?.firstOrNull { !it.isLoopbackAddress }?.hostAddress
                        } catch (_: Exception) { null }
                        if (ip != null) {
                            prefs.edit().putBoolean("create_pending", false).apply()
                            complete(id, "Wi-Fi Direct group ready. Connect Windows to ${group!!.networkName} using password ${group.passphrase}. Start Wi-Fi Bridge, then pair using the QR code. Phone address: $ip", ip)
                            return@requestGroupInfo
                        }
                    }
                    if (attempt < 30) handler.postDelayed({ waitForGroup(id, name, attempt + 1) }, 500)
                    else complete(id, "Group details are not ready. Use Refresh Wi-Fi Direct or check Android Wi-Fi Direct settings.")
                } catch (ex: Exception) { complete(id, "Could not inspect Wi-Fi Direct: ${ex.message}") }
            }
        } catch (ex: Exception) { complete(id, "Could not read Wi-Fi Direct group: ${ex.message}") }
    }

    fun remove() {
        val m = manager; val c = channel
        if (m == null || c == null) { tell("Wi-Fi Direct is not available.", null); return }
        val owned = prefs.getString("group_name", null)
        if (owned == null) { tell("No TetherBridge-owned Wi-Fi Direct group is recorded.", null); return }
        val id = start(); if (id == 0L) return
        try {
            m.requestGroupInfo(c) { group ->
                if (!operations.isCurrent(id)) return@requestGroupInfo
                try {
                    if (group == null && prefs.getBoolean("create_pending", false)) complete(id, "Group creation is unresolved; no group was removed. Refresh later and inspect Android Wi-Fi Direct settings.")
                    else if (group == null) { forgetIfOwned(owned); complete(id, "The TetherBridge Wi-Fi Direct group is already stopped.") }
                    else if (!matches(group, owned)) complete(id, "The active Wi-Fi Direct group does not match TetherBridge's saved identity; it was left running.")
                    else {
                        operations.submitMutation(id)
                        m.removeGroup(c, object : WifiP2pManager.ActionListener {
                            override fun onSuccess() {
                                if (!operations.settleMutation(id)) return
                                forgetIfOwned(owned)
                                complete(id, "TetherBridge Wi-Fi Direct group stopped.")
                            }
                            override fun onFailure(reason: Int) {
                                if (operations.settleMutation(id))
                                    complete(id, "Could not stop Wi-Fi Direct group (Android code $reason).")
                            }
                        })
                    }
                } catch (ex: Exception) {
                    operations.settleMutation(id)
                    complete(id, "Could not stop Wi-Fi Direct: ${ex.message}")
                }
            }
        } catch (ex: Exception) { complete(id, "Could not stop Wi-Fi Direct: ${ex.message}") }
    }

    fun dispose() { disposed = true }
}
