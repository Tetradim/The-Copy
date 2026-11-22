package org.tetherbridge.classicapp

import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

class ConnectionStats {
    private val activeConnections = AtomicInteger(0)
    private val totalConnections = AtomicLong(0)
    private val bytesUp = AtomicLong(0)
    private val bytesDown = AtomicLong(0)
    private val rejectedConnections = AtomicLong(0)
    private val startedAtEpochMs = System.currentTimeMillis()

    fun opened() {
        activeConnections.incrementAndGet()
        totalConnections.incrementAndGet()
    }

    fun rejected() {
        rejectedConnections.incrementAndGet()
    }

    fun closed() {
        activeConnections.updateAndGet { current -> if (current > 0) current - 1 else 0 }
    }

    fun addUp(bytes: Int) {
        if (bytes > 0) bytesUp.addAndGet(bytes.toLong())
    }

    fun addDown(bytes: Int) {
        if (bytes > 0) bytesDown.addAndGet(bytes.toLong())
    }

    fun toJsonFields(): String {
        return "\"activeConnections\":${activeConnections.get()}," +
            "\"totalConnections\":${totalConnections.get()}," +
            "\"rejectedConnections\":${rejectedConnections.get()}," +
            "\"bytesUp\":${bytesUp.get()}," +
            "\"bytesDown\":${bytesDown.get()}," +
            "\"statsStartedAtEpochMs\":$startedAtEpochMs"
    }
}
