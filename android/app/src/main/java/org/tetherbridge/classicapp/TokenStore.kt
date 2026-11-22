package org.tetherbridge.classicapp

import android.content.Context

object TokenStore {
    private const val PREFS = "tetherbridge_runtime"
    private const val KEY_TOKEN = "pairing_token"
    private const val KEY_ACTIVE_MODE = "active_mode"

    fun getOrCreateToken(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val existing = prefs.getString(KEY_TOKEN, null)
        if (!existing.isNullOrBlank()) return existing

        val generated = ProxyConfig.generateToken()
        prefs.edit().putString(KEY_TOKEN, generated).apply()
        return generated
    }

    fun rotateToken(context: Context): String {
        val generated = ProxyConfig.generateToken()
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_TOKEN, generated)
            .apply()
        return generated
    }

    fun setActiveMode(context: Context, mode: BridgeMode) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_ACTIVE_MODE, mode.name)
            .apply()
    }

    fun getActiveMode(context: Context): BridgeMode? {
        val name = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_ACTIVE_MODE, null)
        return name?.let { runCatching { BridgeMode.valueOf(it) }.getOrNull() }
    }

    fun clearActiveMode(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .remove(KEY_ACTIVE_MODE)
            .apply()
    }
}
