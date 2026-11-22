package org.tetherbridge.classicapp

import java.util.UUID

object BluetoothRfcommScaffold {
    // Reserved for v0.7+: this is intentionally not wired into the proxy path yet.
    // The UUID is owned by this clean-room project and does not come from any legacy app.
    val ServiceUuid: UUID = UUID.fromString("7b8f2a00-35be-4fa3-b8d3-4a9d5b96db61")

    fun serviceName(): String = "TetherBridge RFCOMM Bridge"

    fun guidance(): String {
        return "Bluetooth/PAN Assist checks native PAN first. Bluetooth Bridge is available separately for paired devices using the matching Windows app."
    }
}
