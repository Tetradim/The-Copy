package org.tetherbridge.classicapp

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Binder
import android.os.Process
import java.util.Locale

class UsbPairingProvider : ContentProvider() {
    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?
    ): Cursor? {
        requireUsbShellCaller()
        if (uri.path != "/pairing") return null

        val appContext = context ?: return null
        val mode = BridgeMode.WIFI
        val token = TokenStore.getOrCreateToken(appContext)
        val host = WifiAssist.bestGuessPhoneHost(appContext)
        val columns = selectedColumns(projection)
        val values = mapOf(
            "payload" to PairingPayload.build(appContext, mode),
            "host" to host,
            "port" to ProxyConfig.DEFAULT_PHONE_PORT.toString(),
            "token" to token,
            "mode" to mode.name.lowercase(Locale.US),
            "version" to BuildConfig.VERSION_NAME,
            "usbHost" to "127.0.0.1",
            "usbLocalPort" to ProxyConfig.DEFAULT_PHONE_PORT.toString(),
            "packageName" to appContext.packageName
        )

        return MatrixCursor(columns).apply {
            addRow(columns.map { values[it] ?: "" }.toTypedArray())
        }
    }

    override fun getType(uri: Uri): String? {
        requireUsbShellCaller()
        return "vnd.android.cursor.item/vnd.org.tetherbridge.classicapp.usbpairing"
    }

    override fun onCreate(): Boolean = true

    override fun insert(uri: Uri, values: ContentValues?): Uri? {
        requireUsbShellCaller()
        return null
    }

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int {
        requireUsbShellCaller()
        return 0
    }

    override fun update(
        uri: Uri,
        values: ContentValues?,
        selection: String?,
        selectionArgs: Array<out String>?
    ): Int {
        requireUsbShellCaller()
        return 0
    }

    private fun selectedColumns(projection: Array<out String>?): Array<String> {
        val requested = projection
            ?.filter { column -> AllColumns.contains(column) }
            ?.takeIf { it.isNotEmpty() }
            ?.toTypedArray()
        return requested ?: AllColumns
    }

    private fun requireUsbShellCaller() {
        val uid = Binder.getCallingUid()
        if (uid == Process.SHELL_UID || uid == Process.ROOT_UID || uid == Process.SYSTEM_UID) return
        throw SecurityException("USB pairing inject is only available to the USB/ADB shell caller.")
    }

    companion object {
        private val AllColumns = arrayOf(
            "payload",
            "host",
            "port",
            "token",
            "mode",
            "version",
            "usbHost",
            "usbLocalPort",
            "packageName"
        )
    }
}
