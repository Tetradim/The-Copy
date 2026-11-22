package org.tetherbridge.classicapp

import org.junit.Assert.*
import org.junit.Test

class BluetoothProxyConfigTest {
    @Test fun bluetoothBackendIsPrivateAndAuthenticated() {
        val config = ProxyConfig.forMode(BridgeMode.BLUETOOTH_RFCOMM, "secret")
        assertEquals("127.0.0.1", config.listenHost)
        assertEquals(10892, config.listenPort)
        assertTrue(config.requireToken)
    }
}
