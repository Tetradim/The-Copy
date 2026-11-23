package org.tetherbridge.classicapp

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log

class ProxyForegroundService : Service() {
    private var activeConfig: ProxyConfig? = null
    private var proxy: HttpConnectProxy? = null
    private var usbFrames: FramedUsbServer? = null
    private var bluetooth: BluetoothRfcommServer? = null
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "onStartCommand action=${intent?.action ?: "null"} startId=$startId flags=$flags")
        val restoredMode = if (intent == null) TokenStore.getActiveMode(this) else null
        val action = intent?.action ?: if (restoredMode != null) ACTION_START else null
        when (action) {
            ACTION_START -> {
                val modeName = intent?.getStringExtra(EXTRA_MODE) ?: restoredMode?.name ?: BridgeMode.USB.name
                val token = intent?.getStringExtra(EXTRA_TOKEN) ?: TokenStore.getOrCreateToken(this)
                val mode = runCatching { BridgeMode.valueOf(modeName) }.getOrDefault(BridgeMode.USB)
                val config = ProxyConfig.forMode(mode, token)
                TokenStore.setActiveMode(this, mode)
                Log.i(TAG, "START mode=$mode listen=${config.listenHost}:${config.listenPort} requireToken=${config.requireToken} tokenPresent=${token.isNotBlank()}")
                try {
                    startForeground(NOTIFICATION_ID, notification("Bridge running: $mode"))
                } catch (ex: Exception) {
                    Log.e(TAG, "START_FOREGROUND_FAILED mode=$mode error=${ex.javaClass.simpleName}: ${ex.message}", ex)
                    throw ex
                }
                try {
                    if (activeConfig?.canReuseUsb(config, proxy?.isListening == true, usbFrames?.isListening == true) != true)
                        startProxy(config)
                    else
                        Log.i(TAG, "USB_START_ALREADY_RUNNING")
                    acquireBridgeWakeLock(mode)
                    val detail = if (mode == BridgeMode.USB)
                        "Connect with USB Bridge in the Windows app. Windows manages the USB port automatically."
                    else "Phone port: ${config.listenHost}:${config.listenPort}"
                    reportState("Bridge running: $mode\n$detail")
                } catch (ex: Exception) {
                    Log.e(TAG, "BRIDGE_START_FAILED mode=$mode", ex)
                    reportState("Bridge could not start: ${ex.message ?: ex.javaClass.simpleName}")
                    TokenStore.clearActiveMode(this)
                    releaseBridgeWakeLock()
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                    return START_NOT_STICKY
                }
            }
            ACTION_STOP -> {
                Log.i(TAG, "STOP requested")
                stopProxy()
                releaseBridgeWakeLock()
                TokenStore.clearActiveMode(this)
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                reportState("Bridge stopped.")
            }
        }
        return if (action == ACTION_START) START_STICKY else START_NOT_STICKY
    }

    private fun startProxy(config: ProxyConfig) {
        try {
            stopProxy()
            val authenticationRegistry = AuthenticatedSessionRegistry()
            val backend = HttpConnectProxy(config) { port -> authenticationRegistry.markAuthenticated(port) }
            proxy = backend
            backend.start()
            if (config.mode == BridgeMode.USB || config.mode == BridgeMode.BLUETOOTH_RFCOMM) {
                lateinit var frames: FramedUsbServer
                val secure = config.mode == BridgeMode.BLUETOOTH_RFCOMM
                frames = FramedUsbServer(
                    listenPort = if (secure) 10891 else FramedUsbServer.PORT,
                    backendPort = backend.boundPort,
                    secure = secure,
                    authSessions = authenticationRegistry,
                    onFatal = { error ->
                        android.os.Handler(mainLooper).post {
                            if (usbFrames === frames) {
                                stopProxy(); releaseBridgeWakeLock(); TokenStore.clearActiveMode(this)
                                reportState("Bridge listener stopped unexpectedly: $error")
                                stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
                            }
                        }
                    }
                )
                usbFrames = frames
                frames.start()
            }
            if (config.mode == BridgeMode.BLUETOOTH_RFCOMM) {
                lateinit var instance: BluetoothRfcommServer
                instance = BluetoothRfcommServer(this) { error ->
                    android.os.Handler(mainLooper).post {
                        if (bluetooth === instance) {
                            stopProxy(); releaseBridgeWakeLock(); TokenStore.clearActiveMode(this)
                            reportState("Bluetooth bridge stopped unexpectedly: $error")
                            stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
                        }
                    }
                }
                bluetooth = instance
                instance.start()
            }
            activeConfig = config
        } catch (ex: Exception) {
            Log.e(TAG, "PROXY_START_FAILED mode=${config.mode} listen=${config.listenHost}:${config.listenPort} error=${ex.javaClass.simpleName}: ${ex.message}", ex)
            stopProxy()
            throw ex
        }
    }

    private fun stopProxy() {
        activeConfig = null
        Log.i(TAG, "PROXY_STOP")
        val oldBluetooth = bluetooth; bluetooth = null; oldBluetooth?.stop()
        val oldFrames = usbFrames; usbFrames = null; oldFrames?.stop()
        val oldProxy = proxy; proxy = null; oldProxy?.stop()
    }

    private fun reportState(message: String) {
        sendBroadcast(Intent(ACTION_STATE).setPackage(packageName).putExtra(EXTRA_STATE, message))
    }

    private fun acquireBridgeWakeLock(mode: BridgeMode) {
        try {
            val existing = wakeLock
            if (existing?.isHeld == true) {
                Log.i(TAG, "WAKE_LOCK_ALREADY_HELD mode=$mode")
                return
            }
            val powerManager = getSystemService(PowerManager::class.java)
            wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "TetherBridge:ProxyService").apply {
                setReferenceCounted(false)
                acquire()
            }
            Log.i(TAG, "WAKE_LOCK_ACQUIRED mode=$mode")
        } catch (ex: Exception) {
            Log.e(TAG, "WAKE_LOCK_FAILED mode=$mode error=${ex.javaClass.simpleName}: ${ex.message}", ex)
        }
    }

    private fun releaseBridgeWakeLock() {
        try {
            val lock = wakeLock
            if (lock?.isHeld == true) {
                lock.release()
                Log.i(TAG, "WAKE_LOCK_RELEASED")
            }
        } catch (ex: Exception) {
            Log.w(TAG, "WAKE_LOCK_RELEASE_FAILED error=${ex.javaClass.simpleName}: ${ex.message}", ex)
        } finally {
            wakeLock = null
        }
    }

    private fun notification(text: String): Notification {
        val channelId = "tetherbridge"
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(channelId, "TetherBridge", NotificationManager.IMPORTANCE_LOW)
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, channelId)
        } else {
            @Suppress("DEPRECATION") Notification.Builder(this)
        }
        return builder
            .setContentTitle("TetherBridge Classic")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .build()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        Log.i(TAG, "onDestroy")
        stopProxy()
        releaseBridgeWakeLock()
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "org.tetherbridge.classicapp.START"
        const val ACTION_STOP = "org.tetherbridge.classicapp.STOP"
        const val ACTION_STATE = "org.tetherbridge.classicapp.STATE"
        const val EXTRA_STATE = "state"
        const val EXTRA_MODE = "mode"
        const val EXTRA_TOKEN = "token"
        private const val TAG = "TetherBridgeService"
        private const val NOTIFICATION_ID = 3001
    }
}
