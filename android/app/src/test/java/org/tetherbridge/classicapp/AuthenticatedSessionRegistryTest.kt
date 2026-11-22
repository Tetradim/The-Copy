package org.tetherbridge.classicapp

import org.junit.Assert.*
import org.junit.Test

class AuthenticatedSessionRegistryTest {
    @Test fun expiryAndAuthenticationAreMutuallyExclusive() {
        val registry = AuthenticatedSessionRegistry()
        val expired = registry.register(12010)
        assertTrue(expired.expire())
        registry.markAuthenticated(12010)
        assertFalse(expired.isAuthenticated())
        val accepted = registry.register(12011)
        registry.markAuthenticated(12011)
        assertFalse(accepted.expire())
        assertTrue(accepted.isAuthenticated())
    }
    @Test fun authenticatesOnlyTheMatchingLocalConnection() {
        val registry = AuthenticatedSessionRegistry()
        val first = registry.register(12001)
        val second = registry.register(12002)
        registry.markAuthenticated(12001)
        assertTrue(first.isAuthenticated())
        assertFalse(second.isAuthenticated())
        registry.remove(12001, first)
        val replacement = registry.register(12001)
        registry.remove(12001, first)
        registry.markAuthenticated(12001)
        assertTrue(replacement.isAuthenticated())
    }
}
