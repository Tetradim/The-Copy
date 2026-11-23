package org.tetherbridge.classicapp

import org.junit.Assert.*
import org.junit.Test

class UsbBridgeStartPolicyTest {
    @Test fun usbUsesEphemeralBackendAndReusesOnlyHealthyUnchangedConfiguration() {
        val usb = ProxyConfig.forMode(BridgeMode.USB, "test-token")
        assertEquals(0, usb.listenPort)
        assertTrue(usb.canReuseUsb(usb.copy(), true, true))
        assertFalse(usb.canReuseUsb(usb, false, true))
        assertFalse(usb.canReuseUsb(usb, true, false))
        assertFalse(usb.canReuseUsb(usb.copy(token = "rotated"), true, true))
        assertFalse(usb.canReuseUsb(ProxyConfig.forMode(BridgeMode.WIFI, "test-token"), true, true))
    }
}
