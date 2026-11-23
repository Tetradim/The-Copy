package org.tetherbridge.classicapp

import android.app.Activity
import android.app.AlertDialog
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.os.Handler
import android.os.Looper
import java.io.DataOutputStream
import java.util.UUID

/** A short-lived approval channel over an authenticated, encrypted Bluetooth bond. */
internal class BluetoothPairing(private val activity: Activity, private val report: (String) -> Unit) {
    private val handler = Handler(Looper.getMainLooper())
    private var listener: BluetoothServerSocket? = null
    private var client: BluetoothSocket? = null
    private var dialog: AlertDialog? = null
    private val expire = Runnable {
        stop()
        report("Bluetooth pairing expired. Tap Pair with PC to try again.")
    }

    fun start() {
        stop()
        val server = try {
            val adapter = activity.getSystemService(BluetoothManager::class.java)?.adapter
                ?: error("Bluetooth is unavailable.")
            check(adapter.isEnabled) { "Turn on Bluetooth first." }
            adapter.listenUsingRfcommWithServiceRecord("TetherBridge Pairing", serviceId)
        } catch (ex: Exception) {
            report("Could not open Bluetooth pairing. Check Bluetooth and nearby-device permission.")
            return
        }
        listener = server
        report("On Windows, select this phone and click Pair with phone.")
        handler.postDelayed(expire, 60_000)
        Thread({
            try {
                val incoming = server.accept()
                close(server)
                handler.post { confirm(server, incoming) }
            } catch (ex: Exception) {
                handler.post {
                    if (listener === server) {
                        stop()
                        report("Bluetooth pairing listener closed. Tap Pair with PC to try again.")
                    }
                }
            }
        }, "tb-bt-pair-listener").start()
    }

    private fun confirm(server: BluetoothServerSocket, incoming: BluetoothSocket) {
        if (listener !== server) {
            close(incoming)
            return
        }
        client = incoming
        val peer = incoming.remoteDevice
        val name = try {
            check(peer.bondState == BluetoothDevice.BOND_BONDED)
            peer.name?.takeIf { it.isNotBlank() } ?: peer.address
        } catch (ex: Exception) {
            stop()
            report("Pair the computer in Android Bluetooth settings first.")
            return
        }
        dialog = AlertDialog.Builder(activity)
            .setTitle("Allow TetherBridge pairing?")
            .setMessage("Allow the paired computer $name to use TetherBridge?")
            .setPositiveButton("Allow") { _, _ -> allow(server, incoming) }
            .setNegativeButton("Deny") { _, _ -> deny() }
            .setOnCancelListener { deny() }
            .show()
    }

    private fun allow(server: BluetoothServerSocket, incoming: BluetoothSocket) {
        val payload = try {
            PairingPayload.build(activity, BridgeMode.BLUETOOTH_RFCOMM, "localhost").toByteArray(Charsets.UTF_8)
        } catch (ex: Exception) {
            stop()
            report("Could not prepare Bluetooth pairing.")
            return
        }
        Thread({
            val delivered = runCatching {
                DataOutputStream(incoming.outputStream).apply {
                    writeInt(payload.size)
                    write(payload)
                    flush()
                }
            }.isSuccess
            handler.post {
                if (listener === server) {
                    stop()
                    report(if (delivered) "Computer approved. Start Bluetooth Bridge, then click Connect on Windows."
                        else "The computer disconnected. Tap Pair with PC to try again.")
                }
            }
        }, "tb-bt-pair-response").start()
    }

    private fun deny() {
        stop()
        report("Bluetooth pairing declined.")
    }

    fun stop() {
        handler.removeCallbacks(expire)
        val previous = listener
        listener = null
        close(previous)
        close(client)
        client = null
        dialog?.dismiss()
        dialog = null
    }

    private fun close(socket: java.io.Closeable?) {
        runCatching { socket?.close() }
    }

    private companion object {
        val serviceId: UUID = UUID.fromString("953d7f10-3df7-4a8c-a0e7-85744b080d20")
    }
}
