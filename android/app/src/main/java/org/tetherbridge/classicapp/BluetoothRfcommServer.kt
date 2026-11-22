package org.tetherbridge.classicapp

import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.util.Log
import java.net.InetSocketAddress
import java.net.Socket
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

// RFCOMM link is authenticated/encrypted by Android. The forwarded bytes also carry
// the existing pinned TLS and token-authenticated protocol to a loopback listener.
class BluetoothRfcommServer(private val context: Context, private val onFatal: (String) -> Unit) {
    private val running = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val clients = ConcurrentHashMap.newKeySet<BluetoothSocket>()
    private val targets = ConcurrentHashMap.newKeySet<Socket>()
    private val workers = ThreadPoolExecutor(0, 16, 30, TimeUnit.SECONDS, SynchronousQueue<Runnable>(), ThreadPoolExecutor.AbortPolicy())
    private val servers = mutableListOf<BluetoothServerSocket>()
    private val acceptThreads = mutableListOf<Thread>()

    @Synchronized fun start() {
        check(started.compareAndSet(false, true)) { "Bluetooth server instances cannot be restarted" }
        val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter ?: error("Bluetooth is unavailable")
        check(adapter.isEnabled) { "Turn on Bluetooth first" }
        try {
            for (slot in 0 until SERVICE_SLOTS) {
                val listener = adapter.listenUsingRfcommWithServiceRecord("TetherBridge ${slot + 1}", serviceUuid(slot))
                servers.add(listener)
            }
        } catch (ex: Exception) {
            servers.forEach { try { it.close() } catch (_: Exception) { } }; servers.clear(); throw ex
        }
        running.set(true)
        servers.forEachIndexed { slot, listener ->
            val thread = Thread({
                while (running.get()) {
                    try {
                        val client = listener.accept()
                        clients.add(client)
                        if (!running.get()) { clients.remove(client); client.close(); continue }
                        try { workers.execute { bridge(client) } }
                        catch (_: Exception) { clients.remove(client); client.close() }
                    } catch (ex: Exception) { if (running.get()) { Log.e(TAG, "Bluetooth accept failed", ex); stop(); onFatal(ex.javaClass.simpleName) } }
                }
            }, "tb-bt-accept-$slot").apply { isDaemon = true; start() }
            acceptThreads.add(thread)
        }
    }

    fun stop() {
        running.set(false)
        servers.forEach { try { it.close() } catch (_: Exception) { } }
        clients.forEach { try { it.close() } catch (_: Exception) { } }
        targets.forEach { try { it.close() } catch (_: Exception) { } }
        workers.shutdownNow(); acceptThreads.forEach { it.interrupt() }
    }

    private fun bridge(client: BluetoothSocket) {
        val target = Socket()
        try {
            targets.add(target)
            if (!running.get()) return
            target.connect(InetSocketAddress("127.0.0.1", 10891), 3000)
            val done = CountDownLatch(2)
            fun closeBoth() { try { client.close() } catch (_: Exception) { }; try { target.close() } catch (_: Exception) { } }
            Thread({
                try { client.inputStream.copyTo(target.getOutputStream()); target.shutdownOutput() }
                catch (_: Exception) { closeBoth() } finally { done.countDown() }
            }, "tb-bt-to-tls").apply { isDaemon = true; start() }
            Thread({
                try { target.getInputStream().copyTo(client.outputStream); client.outputStream.flush() }
                catch (_: Exception) { } finally { closeBoth(); done.countDown() }
            }, "tb-tls-to-bt").apply { isDaemon = true; start() }
            done.await()
        } catch (ex: Exception) { if (running.get()) Log.w(TAG, "Bluetooth session failed: ${ex.javaClass.simpleName}") }
        finally { clients.remove(client); targets.remove(target); try { client.close() } catch (_: Exception) { }; try { target.close() } catch (_: Exception) { } }
    }

    companion object {
        const val SERVICE_SLOTS = 8
        fun serviceUuid(slot: Int): UUID {
            require(slot in 0 until SERVICE_SLOTS)
            return UUID.fromString("953d7f10-3df7-4a8c-a0e7-85744b080d1${(8 + slot).toString(16)}")
        }
        private const val TAG = "TetherBridgeBluetooth"
    }
}
