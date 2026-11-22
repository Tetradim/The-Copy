package org.tetherbridge.classicapp

import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import kotlin.math.min

/**
 * Per-peer authentication penalty.
 *
 * Delay calculation uses concurrent per-source counters, then sleeps outside any
 * shared lock so one abusive or mistyped client cannot stall unrelated clients.
 */
class AuthFailureBackoff {
    private val failures = ConcurrentHashMap<String, AtomicInteger>()

    fun delay(source: String) {
        val count = failures.computeIfAbsent(source) { AtomicInteger(0) }.incrementAndGet()
        val cappedShift = min(count - 1, 4)
        val delayMs = min(2000L, 150L * (1L shl cappedShift))
        try {
            Thread.sleep(delayMs)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
    }

    fun reset(source: String) {
        failures.remove(source)
    }
}
