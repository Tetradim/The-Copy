package org.tetherbridge.classicapp

import android.util.Base64
import android.util.Log
import java.io.BufferedOutputStream
import java.io.ByteArrayInputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.SequenceInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.URI
import java.security.MessageDigest
import javax.net.ssl.SSLServerSocket
import java.util.Locale
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.min

class HttpConnectProxy(private val config: ProxyConfig, private val onAuthenticated: (Int) -> Unit = {}) {
    private val running = AtomicBoolean(false)
    private val clients = ConcurrentHashMap.newKeySet<Socket>()
    // Never read a client's header on the accept thread: a stalled client must
    // not prevent health checks or other connections from being accepted.
    private val headerPool = ThreadPoolExecutor(
        0, 32, 30L, TimeUnit.SECONDS,
        SynchronousQueue<Runnable>(), ThreadPoolExecutor.AbortPolicy()
    )
    private val pool = ThreadPoolExecutor(
        4,
        64,
        30L,
        TimeUnit.SECONDS,
        LinkedBlockingQueue<Runnable>(256),
        ThreadPoolExecutor.AbortPolicy()
    ).apply {
        allowCoreThreadTimeOut(true)
    }
    private val controlPool = ThreadPoolExecutor(
        1,
        4,
        10L,
        TimeUnit.SECONDS,
        LinkedBlockingQueue<Runnable>(64),
        ThreadPoolExecutor.AbortPolicy()
    ).apply {
        allowCoreThreadTimeOut(true)
    }
    private val failedAuthBackoff = AuthFailureBackoff()
    private val stats = ConnectionStats()
    private val startedAtMillis = System.currentTimeMillis()
    private var server: ServerSocket? = null
    private var acceptThread: Thread? = null

    @Synchronized
    fun start() {
        if (running.get()) return
        val bind = InetAddress.getByName(config.listenHost)
        val listener = if (config.mode == BridgeMode.USB || config.mode == BridgeMode.BLUETOOTH_RFCOMM) ServerSocket() else WirelessTls.serverContext().serverSocketFactory.createServerSocket()
        try {
            if (listener is SSLServerSocket) {
                listener.enabledProtocols = listener.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                check(listener.enabledProtocols.isNotEmpty()) { "TLS 1.2 or later is required" }
            }
            listener.reuseAddress = true
            listener.bind(InetSocketAddress(bind, config.listenPort), 64)
        } catch (ex: Exception) {
            listener.close()
            throw ex
        }
        server = listener
        running.set(true)
        acceptThread = Thread({ listenLoop() }, "tb-accept-${config.mode}").apply {
            isDaemon = true
            start()
        }
    }

    private fun listenLoop() {
        try {
            Log.i(TAG, "LISTENER_STARTED mode=${config.mode} bind=${config.listenHost}:${config.listenPort} requireToken=${config.requireToken}")
            while (running.get()) {
                val client = server!!.accept()
                clients.add(client)
                if (!running.get()) {
                    clients.remove(client)
                    closeQuietly(client)
                    continue
                }
                try {
                    headerPool.execute { dispatchPreparedClient(client) }
                } catch (_: RejectedExecutionException) {
                    stats.rejected()
                    clients.remove(client)
                    closeQuietly(client)
                }
            }
        } catch (ex: SocketException) {
            if (running.get()) {
                Log.e(TAG, "LISTENER_SOCKET_ERROR mode=${config.mode} bind=${config.listenHost}:${config.listenPort} error=${ex.javaClass.simpleName}: ${ex.message}", ex)
            }
            running.set(false)
        } catch (ex: Exception) {
            Log.e(TAG, "LISTENER_FATAL mode=${config.mode} bind=${config.listenHost}:${config.listenPort} error=${ex.javaClass.simpleName}: ${ex.message}", ex)
            stop()
        }
    }

    fun stop() {
        Log.i(TAG, "LISTENER_STOP mode=${config.mode} bind=${config.listenHost}:${config.listenPort}")
        running.set(false)
        try { server?.close() } catch (_: Exception) {}
        clients.forEach { closeQuietly(it) }
        acceptThread?.interrupt()
        acceptThread = null
        headerPool.shutdownNow()
        controlPool.shutdownNow()
        pool.shutdownNow()
    }

    private fun dispatchPreparedClient(client: Socket) {
        stats.opened()
        var scheduled = false
        var remoteAddress = client.inetAddress.hostAddress ?: "unknown"
        var requestLine = "unread"
        try {
            client.tcpNoDelay = true
            client.soTimeout = HEADER_READ_TIMEOUT_MS
            val headerRead = readHeader(client.getInputStream())
            if (headerRead == null) {
                Log.w(TAG, "CLIENT_EMPTY remote=$remoteAddress")
                return
            }

            val header = headerRead.header.toString(Charsets.ISO_8859_1)
            val lines = header.split("\r\n")
            requestLine = lines.firstOrNull().orEmpty()
            remoteAddress = client.inetAddress.hostAddress ?: "unknown"
            val controlRequest = isControlRequest(requestLine)
            if (!controlRequest) client.soTimeout = 0

            val executor = if (controlRequest) controlPool else pool
            executor.execute {
                try {
                    if (controlRequest) {
                        handleControlRequest(client, lines, requestLine)
                    } else {
                        handlePreparedClient(client, headerRead, lines, requestLine)
                    }
                } finally {
                    clients.remove(client)
                    stats.closed()
                }
            }
            scheduled = true
        } catch (ex: RejectedExecutionException) {
            stats.rejected()
            Log.w(TAG, "CLIENT_REJECTED_OVERLOAD remote=$remoteAddress request=\"$requestLine\" error=${ex.javaClass.simpleName}: ${ex.message}")
            writeServiceUnavailable(client)
        } catch (ex: Exception) {
            Log.w(TAG, "CLIENT_DISPATCH_ERROR remote=$remoteAddress request=\"$requestLine\" error=${ex.javaClass.simpleName}: ${ex.message}", ex)
        } finally {
            if (!scheduled) {
                clients.remove(client)
                closeQuietly(client)
                stats.closed()
            }
        }
    }

    private fun handleControlRequest(client: Socket, lines: List<String>, requestLine: String) {
        val remoteAddress = client.inetAddress.hostAddress ?: "unknown"
        try {
            client.use { c ->
                val output = BufferedOutputStream(c.getOutputStream())
                if (requestLine.startsWith("GET /health", ignoreCase = true)) {
                    if (config.requireToken && !authorized(lines)) {
                        rejectUnauthorized(output, remoteAddress)
                        return
                    }
                    failedAuthBackoff.reset(remoteAddress)
                    onAuthenticated(c.port)
                    writeText(output, statusResponse(includeCounters = false))
                    return
                }

                if (requestLine.startsWith("GET /status", ignoreCase = true)) {
                    if (!authorized(lines)) {
                        rejectUnauthorized(output, remoteAddress)
                        return
                    }
                    failedAuthBackoff.reset(remoteAddress)
                    onAuthenticated(c.port)
                    writeText(output, statusResponse(includeCounters = true))
                    return
                }

                writeText(output, "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
            }
        } catch (ex: Exception) {
            Log.w(TAG, "CONTROL_ERROR remote=$remoteAddress request=\"$requestLine\" error=${ex.javaClass.simpleName}: ${ex.message}", ex)
        }
    }

    private fun handlePreparedClient(client: Socket, headerRead: HeaderReadResult, lines: List<String>, requestLine: String) {
        var remoteAddress = client.inetAddress.hostAddress ?: "unknown"
        try {
            client.use { c ->
                val input = c.getInputStream()
                val output = BufferedOutputStream(c.getOutputStream())
                remoteAddress = c.inetAddress.hostAddress ?: "unknown"

                if (!authorized(lines)) {
                    rejectUnauthorized(output, remoteAddress)
                    return
                }
                failedAuthBackoff.reset(remoteAddress)
                onAuthenticated(c.port)

                if (requestLine.uppercase(Locale.US).startsWith("CONNECT-UDP ")) {
                    val target = requestLine.split(" ").getOrNull(1) ?: return
                    val authority = ConnectAuthority.parse(target)
                    if (authority == null) {
                        writeText(output, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
                        return
                    }
                    tunnelDatagrams(authority.host, authority.port, output, c, input, headerRead.tail)
                } else if (requestLine.uppercase(Locale.US).startsWith("CONNECT ")) {
                    val target = requestLine.split(" ").getOrNull(1) ?: return
                    val authority = ConnectAuthority.parse(target)
                    if (authority == null) {
                        writeText(output, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
                        return
                    }
                    tunnelConnect(authority.host, authority.port, output, c, input, headerRead.tail)
                } else {
                    forwardHttp(requestLine, headerRead.header, headerRead.tail, output, c, input)
                }
            }
        } catch (ex: Exception) {
            Log.w(TAG, "CLIENT_ERROR remote=$remoteAddress request=\"$requestLine\" error=${ex.javaClass.simpleName}: ${ex.message}", ex)
        }
    }

    private fun isControlRequest(requestLine: String): Boolean {
        return requestLine.startsWith("GET /health", ignoreCase = true) ||
            requestLine.startsWith("GET /status", ignoreCase = true)
    }

    private fun authorized(lines: List<String>): Boolean {
        if (!config.requireToken) return true
        val tokenHeader = lines.firstOrNull { it.startsWith("X-TetherBridge-Token:", ignoreCase = true) }
            ?.substringAfter(":")
            ?.trim()
        if (constantTimeEquals(tokenHeader, config.token)) return true

        val auth = lines.firstOrNull { it.startsWith("Proxy-Authorization:", ignoreCase = true) }
            ?.substringAfter(":")
            ?.trim()
            .orEmpty()
        if (auth.startsWith("Basic ", ignoreCase = true)) {
            val decoded = try {
                String(Base64.decode(auth.substringAfter(" "), Base64.DEFAULT), Charsets.UTF_8)
            } catch (_: Exception) {
                ""
            }
            return constantTimeEquals(decoded, "tb:${config.token}")
        }
        return false
    }

    private fun rejectUnauthorized(output: BufferedOutputStream, remoteAddress: String) {
        Log.w(TAG, "AUTH_REJECTED remote=$remoteAddress mode=${config.mode} requireToken=${config.requireToken}")
        failedAuthBackoff.delay(remoteAddress)
        writeText(output, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"TetherBridge\"\r\nConnection: close\r\n\r\n")
    }

    private fun tunnelDatagrams(host: String, port: Int, clientOut: BufferedOutputStream, client: Socket, clientInput: InputStream, tail: ByteArray) {
        DatagramSocket().use { udp ->
            val destination = InetAddress.getByName(host)
            writeText(clientOut, "HTTP/1.1 200 Connection Established\r\n\r\n")
            val incoming = DataInputStream(SequenceInputStream(ByteArrayInputStream(tail), clientInput))
            val sender = Thread({
                try {
                    while (true) {
                        val size = incoming.readInt()
                        if (size < 0 || size > 65507) throw java.io.IOException("Invalid UDP datagram size")
                        val data = ByteArray(size)
                        incoming.readFully(data)
                        udp.send(DatagramPacket(data, data.size, destination, port))
                        stats.addUp(data.size)
                    }
                } catch (_: Exception) {
                } finally {
                    udp.close()
                    try { client.close() } catch (_: Exception) {}
                }
            }, "tb-udp-send").apply { isDaemon = true; start() }
            try {
                val out = DataOutputStream(client.getOutputStream())
                val buffer = ByteArray(65507)
                while (!udp.isClosed) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    udp.receive(packet)
                    if (packet.address != destination || packet.port != port) continue
                    out.writeInt(packet.length)
                    out.write(packet.data, packet.offset, packet.length)
                    out.flush()
                    stats.addDown(packet.length)
                }
            } finally {
                udp.close()
                try { client.close() } catch (_: Exception) {}
                sender.interrupt()
            }
        }
    }

    private fun tunnelConnect(host: String, port: Int, clientOut: BufferedOutputStream, client: Socket, clientInput: InputStream, tail: ByteArray) {
        Socket(host, port).use { remote ->
            remote.tcpNoDelay = true
            writeText(clientOut, "HTTP/1.1 200 Connection Established\r\n\r\n")
            if (tail.isNotEmpty()) {
                remote.getOutputStream().write(tail)
                remote.getOutputStream().flush()
                stats.addUp(tail.size)
            }
            pipeBoth(clientInput, client.getOutputStream(), remote.getInputStream(), remote.getOutputStream(), client, remote)
        }
    }

    private fun forwardHttp(requestLine: String, firstHeader: ByteArray, tail: ByteArray, clientOut: BufferedOutputStream, client: Socket, clientInput: InputStream) {
        val targetUri = requestLine.split(" ").getOrNull(1)?.let { runCatching { URI(it) }.getOrNull() }
        val host = targetUri?.host ?: return
        val port = if (targetUri.port > 0) targetUri.port else 80
        Socket(host, port).use { remote ->
            remote.tcpNoDelay = true
            val remoteOut = remote.getOutputStream()
            val rewrittenHeader = rewriteForOriginForm(requestLine, firstHeader)
            remoteOut.write(rewrittenHeader)
            stats.addUp(rewrittenHeader.size)
            if (tail.isNotEmpty()) {
                // Preserve request-body bytes that arrived in the same read as the header block.
                remoteOut.write(tail)
                stats.addUp(tail.size)
            }
            remoteOut.flush()
            // Continue pumping from the same client input stream used for header parsing.
            // Do not re-open client.getInputStream() here, or buffered/over-read body bytes can be stranded.
            pipeBoth(clientInput, clientOut, remote.getInputStream(), remoteOut, client, remote)
        }
    }

    private data class HeaderReadResult(val header: ByteArray, val tail: ByteArray)

    private fun readHeader(input: InputStream): HeaderReadResult? {
        val header = ByteArrayOutputStream()
        val readBuffer = ByteArray(4096)
        val terminator = byteArrayOf('\r'.code.toByte(), '\n'.code.toByte(), '\r'.code.toByte(), '\n'.code.toByte())
        var matched = 0

        while (header.size() < MAX_HEADER_BYTES) {
            val remaining = MAX_HEADER_BYTES - header.size()
            val n = input.read(readBuffer, 0, min(readBuffer.size, remaining))
            if (n < 0) return null

            for (i in 0 until n) {
                val b = readBuffer[i]
                header.write(b.toInt())
                matched = if (b == terminator[matched]) {
                    matched + 1
                } else if (b == terminator[0]) {
                    1
                } else {
                    0
                }
                if (matched == terminator.size) {
                    val tail = if (i + 1 < n) readBuffer.copyOfRange(i + 1, n) else ByteArray(0)
                    return HeaderReadResult(header.toByteArray(), tail)
                }
            }
        }
        return null
    }

    private fun pipeBoth(
        clientInput: InputStream,
        clientOutput: OutputStream,
        remoteInput: InputStream,
        remoteOutput: OutputStream,
        client: Socket,
        remote: Socket
    ) {
        // EOF in one direction is a TCP half-close, not the end of the reply.
        val done = CountDownLatch(2)
        Thread { pump(clientInput, remoteOutput, remote, done, stats::addUp) }.apply {
            name = "tb-client-to-remote"
            isDaemon = true
            start()
        }
        Thread { pump(remoteInput, clientOutput, client, done, stats::addDown) }.apply {
            name = "tb-remote-to-client"
            isDaemon = true
            start()
        }
        done.await()
        try { client.close() } catch (_: Exception) {}
        try { remote.close() } catch (_: Exception) {}
    }

    private fun pump(input: InputStream, output: OutputStream, destination: Socket, done: CountDownLatch, countBytes: (Int) -> Unit) {
        val buf = ByteArray(32 * 1024)
        try {
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                output.write(buf, 0, n)
                output.flush()
                countBytes(n)
            }
            try { destination.shutdownOutput() } catch (_: Exception) {}
        } catch (_: Exception) {
            // On an actual I/O failure, unblock the opposite pump as well.
            try { input.close() } catch (_: Exception) {}
            try { destination.close() } catch (_: Exception) {}
        } finally {
            done.countDown()
        }
    }

    private fun rewriteForOriginForm(requestLine: String, firstHeader: ByteArray): ByteArray {
        val header = firstHeader.toString(Charsets.ISO_8859_1)
        val withoutProxyHeaders = header
            .replace(Regex("(?im)^X-TetherBridge-Token:.*\\r?\\n"), "")
            .replace(Regex("(?im)^Proxy-Authorization:.*\\r?\\n"), "")
            .replace(Regex("(?im)^Proxy-Connection:.*\\r?\\n"), "")
            .replace(Regex("(?im)^Connection:.*\\r?\\n"), "")
            .replace("\r\n\r\n", "\r\nConnection: close\r\n\r\n")

        val parts = requestLine.split(" ", limit = 3)
        if (parts.size < 3) return withoutProxyHeaders.toByteArray(Charsets.ISO_8859_1)

        val uri = runCatching { URI(parts[1]) }.getOrNull() ?: return withoutProxyHeaders.toByteArray(Charsets.ISO_8859_1)
        val originTarget = (uri.rawPath?.takeIf { it.isNotBlank() } ?: "/") + (uri.rawQuery?.let { "?$it" } ?: "")
        val newRequestLine = "${parts[0]} $originTarget ${parts[2]}"
        return withoutProxyHeaders.replaceFirst(requestLine, newRequestLine).toByteArray(Charsets.ISO_8859_1)
    }

    // Status JSON markers: "version":the build version, "listenHost", "timestampMs".
    // status JSON includes "version":the build version for Windows compatibility checks.
    private fun statusResponse(includeCounters: Boolean): String {
        val poolStats = "\"poolActive\":${pool.activeCount}," +
            "\"poolQueueDepth\":${pool.queue.size}," +
            "\"controlActive\":${controlPool.activeCount}," +
            "\"controlQueueDepth\":${controlPool.queue.size}"
        val counters = if (includeCounters) ",${stats.toJsonFields()},$poolStats" else ""
        val now = System.currentTimeMillis()
        val body = "{\"ok\":true," +
            "\"app\":\"TetherBridge\"," +
            "\"version\":\"${BuildConfig.VERSION_NAME}\"," +
            "\"capabilities\":[\"tcp\",\"udp\"]," +
            "\"mode\":\"${config.mode}\"," +
            "\"listenHost\":\"${config.listenHost}\"," +
            "\"proxyPort\":${config.listenPort}," +
            "\"uptimeMs\":${now - startedAtMillis}," +
            "\"timestampMs\":$now$counters}"
        return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${body.toByteArray(Charsets.UTF_8).size}\r\nConnection: close\r\n\r\n$body"
    }

    private fun constantTimeEquals(left: String?, right: String): Boolean {
        if (left == null) return false
        return MessageDigest.isEqual(left.toByteArray(Charsets.UTF_8), right.toByteArray(Charsets.UTF_8))
    }

    private fun writeText(out: BufferedOutputStream, text: String) {
        out.write(text.toByteArray(Charsets.ISO_8859_1))
        out.flush()
    }

    private fun writeServiceUnavailable(socket: Socket) {
        try {
            BufferedOutputStream(socket.getOutputStream()).use { output ->
                writeText(output, "HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
            }
        } catch (_: Exception) {
        }
    }

    private fun closeQuietly(socket: Socket) {
        try { socket.close() } catch (_: Exception) {}
    }

    companion object {
        private const val TAG = "TetherBridgeProxy"
        private const val MAX_HEADER_BYTES = 64 * 1024
        private const val HEADER_READ_TIMEOUT_MS = 5_000
    }
}
