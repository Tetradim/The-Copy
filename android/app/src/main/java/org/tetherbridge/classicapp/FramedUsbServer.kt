package org.tetherbridge.classicapp

import android.util.Log
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Loopback-only framed USB transport: zero-length records mean write EOF. */
class FramedUsbServer(
    private val listenPort: Int = PORT,
    private val backendPort: Int = ProxyConfig.USB_HTTP_PORT,
    private val secure: Boolean = false,
    private val authSessions: AuthenticatedSessionRegistry = AuthenticatedSessionRegistry(),
    private val onFatal: (String) -> Unit = {}
) {
    private val running = AtomicBoolean(false)
    private val sockets = ConcurrentHashMap.newKeySet<Socket>()
    private val deadlines = java.util.concurrent.ScheduledThreadPoolExecutor(1)
    private val workers = ThreadPoolExecutor(0, 32, 30, TimeUnit.SECONDS, SynchronousQueue<Runnable>(), ThreadPoolExecutor.AbortPolicy())
    private var listener: ServerSocket? = null
    private var acceptThread: Thread? = null

    fun start() {
        val server = if (secure) WirelessTls.serverContext().serverSocketFactory.createServerSocket() else ServerSocket()
        try {
            if (server is javax.net.ssl.SSLServerSocket) {
                server.enabledProtocols = server.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                check(server.enabledProtocols.isNotEmpty()) { "TLS 1.2 or later is required" }
            }
            server.reuseAddress = true
            server.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), listenPort), 64)
        } catch (ex: Exception) { server.close(); throw ex }
        listener = server
        running.set(true)
        acceptThread = Thread({
            while (running.get()) {
                try {
                    val client = server.accept()
                    sockets.add(client)
                    if (!running.get()) { sockets.remove(client); client.close(); continue }
                    try { workers.execute { session(client) } }
                    catch (_: Exception) { sockets.remove(client); client.close() }
                } catch (ex: Exception) {
                    if (running.get()) { Log.w(TAG, "FRAME_ACCEPT_ERROR: ${ex.message}"); stop(); onFatal(ex.javaClass.simpleName) }
                }
            }
        }, "tb-usb-frame-accept").apply { isDaemon = true; start() }
    }

    fun stop() {
        running.set(false)
        try { listener?.close() } catch (_: Exception) {}
        sockets.forEach { try { it.close() } catch (_: Exception) {} }
        deadlines.shutdownNow()
        workers.shutdownNow()
        acceptThread?.interrupt()
    }

    private fun session(client: Socket) {
        var backend: Socket? = null
        try {
            backend = Socket()
            sockets.add(backend)
            if (!running.get()) return
            backend.connect(InetSocketAddress("127.0.0.1", backendPort), 3000)
            val target = backend
            val done = CountDownLatch(2)
            fun fail() { try { client.close() } catch (_: Exception) {}; try { target.close() } catch (_: Exception) {} }
            val backendLocalPort = target.localPort
            val authenticated = if (secure) authSessions.register(backendLocalPort) else null
            val deadline = if (authenticated != null) deadlines.schedule({ if (authenticated.expire()) fail() }, 15, TimeUnit.SECONDS) else null
            Thread({
                try {
                    val input = DataInputStream(client.getInputStream())
                    val output = target.getOutputStream()
                    val buffer = ByteArray(MAX_FRAME)
                    while (true) {
                        val size = input.readInt()
                        if (size == 0) { target.shutdownOutput(); break }
                        if (size < 0 || size > MAX_FRAME) throw java.io.IOException("Invalid USB frame length")
                        input.readFully(buffer, 0, size)
                        output.write(buffer, 0, size)
                        output.flush()
                    }
                } catch (_: Exception) { fail() } finally { done.countDown() }
            }, "tb-usb-frame-in").apply { isDaemon = true; start() }
            Thread({
                try {
                    val input = target.getInputStream()
                    val output = DataOutputStream(client.getOutputStream())
                    val buffer = ByteArray(MAX_FRAME)
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) { output.writeInt(0); output.flush(); break }
                        output.writeInt(count); output.write(buffer, 0, count); output.flush()
                    }
                } catch (_: Exception) { fail() } finally { done.countDown() }
            }, "tb-usb-frame-out").apply { isDaemon = true; start() }
            try { done.await() } finally { deadline?.cancel(false); if (authenticated != null) authSessions.remove(backendLocalPort, authenticated) }
        } catch (ex: Exception) {
            if (running.get() && ex !is EOFException) Log.w(TAG, "USB_SESSION_ERROR: ${ex.message}")
        } finally {
            try { client.close() } catch (_: Exception) {}
            try { backend?.close() } catch (_: Exception) {}
            sockets.remove(client)
            if (backend != null) sockets.remove(backend)
        }
    }

    companion object {
        const val PORT = 10889
        private const val MAX_FRAME = 32768
        private const val TAG = "TetherBridgeUsbFrames"
    }
}
