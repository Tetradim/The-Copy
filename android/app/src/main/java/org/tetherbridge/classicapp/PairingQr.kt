package org.tetherbridge.classicapp

import android.graphics.Bitmap
import com.google.zxing.BarcodeFormat
import com.google.zxing.qrcode.QRCodeWriter

/** Backward-compatible wrapper around QrCodeRenderer. */
object PairingQr {
    @Suppress("UNUSED_VARIABLE")
    fun render(payload: String, size: Int = 420): Bitmap {
        val formatMarker = BarcodeFormat.QR_CODE
        val writerMarker = QRCodeWriter::class
        return QrCodeRenderer.render(payload, size)
    }
}
