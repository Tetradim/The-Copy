package org.tetherbridge.classicapp

import java.util.concurrent.ConcurrentHashMap

class AuthenticatedSession {
    private var accepted = false
    private var expired = false
    @Synchronized fun authenticate() { if (!expired) accepted = true }
    @Synchronized fun isAuthenticated(): Boolean = accepted
    @Synchronized fun expire(): Boolean {
        if (accepted || expired) return false
        expired = true
        return true
    }
}
class AuthenticatedSessionRegistry {
    private val sessions = ConcurrentHashMap<Int, AuthenticatedSession>()
    fun register(port: Int): AuthenticatedSession = AuthenticatedSession().also { sessions[port] = it }
    fun markAuthenticated(port: Int) { sessions[port]?.authenticate() }
    fun remove(port: Int, expected: AuthenticatedSession) { sessions.remove(port, expected) }
}
