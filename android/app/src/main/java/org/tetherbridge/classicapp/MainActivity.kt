package org.tetherbridge.classicapp

import android.Manifest
import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class MainActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var pairingText: TextView
    private lateinit var qrView: ImageView
    private lateinit var direct: WifiDirectController
    private lateinit var bluetoothPairing: BluetoothPairing
    private var directHost: String? = null
    private val stateReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            intent?.getStringExtra(ProxyForegroundService.EXTRA_STATE)?.let { status.text = it }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestNotificationPermissionIfNeeded()

        val scroll = ScrollView(this)
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(32, 48, 32, 48)
        }

        status = TextView(this).apply {
            text = "TetherBridge Classic ${BuildConfig.VERSION_NAME}\nToken: ${TokenStore.getOrCreateToken(this@MainActivity)}\nChoose a mode to start."
            textSize = 16f
        }

        bluetoothPairing = BluetoothPairing(this) { status.text = it }
        direct = WifiDirectController(this) { message, host ->
            if (!isFinishing && !isDestroyed) {
                directHost = host
                status.text = message
                updatePairingViews(BridgeMode.WIFI)
            }
        }

        val guidance = TextView(this).apply {
            text = "Wi-Fi Direct and Bluetooth bridges require pairing with Windows.\n" +
                "For Samsung/One UI, keep notifications allowed and set battery usage to unrestricted so the OS does not freeze the bridge while Windows is connected.\n" +
                WifiAssist.guidance(this@MainActivity) + "\n" + WifiDirectAssist.guidance(this@MainActivity) + "\n" + BluetoothRfcommScaffold.guidance()
            textSize = 13f
        }

        qrView = ImageView(this).apply {
            adjustViewBounds = true
            maxWidth = 420
            maxHeight = 420
        }

        pairingText = TextView(this).apply {
            textSize = 12f
            setTextIsSelectable(true)
        }

        val usb = Button(this).apply {
            text = "Start USB Bridge"
            setOnClickListener { startBridge(BridgeMode.USB) }
        }
        val wifi = Button(this).apply {
            text = "Start Wi-Fi Bridge"
            setOnClickListener { startBridge(BridgeMode.WIFI) }
        }
        val pan = Button(this).apply {
            text = "Bluetooth/PAN Assist"
            setOnClickListener { startBridge(BridgeMode.BLUETOOTH_PAN) }
        }
        val bluetoothBridge = Button(this).apply {
            text = "Start Bluetooth Bridge"
            setOnClickListener { requestBluetoothStart() }
        }
        val pairBluetooth = Button(this).apply {
            text = "Pair with PC"
            setOnClickListener {
                if (Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
                    bluetoothPairing.start()
                else requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), REQUEST_BLUETOOTH_PAIR)
            }
        }
        val directStart = Button(this).apply {
            text = "Create Wi-Fi Direct Group"
            setOnClickListener { requestWifiDirectStart() }
        }
        val directRefresh = Button(this).apply {
            text = "Refresh Wi-Fi Direct Group"
            setOnClickListener { direct.refresh() }
        }
        val directStop = Button(this).apply {
            text = "Stop Wi-Fi Direct Group"
            setOnClickListener { direct.remove() }
        }
        val copyPayload = Button(this).apply {
            text = "Copy Pairing Payload"
            setOnClickListener { copyPairingPayload() }
        }
        val usbPairingInject = Button(this).apply {
            text = "USB Pairing Inject"
            setOnClickListener { prepareUsbPairingInject() }
        }
        val wifiSettings = Button(this).apply {
            text = "Open Wi-Fi / Hotspot Settings"
            setOnClickListener { openWifiSettings() }
        }
        val btSettings = Button(this).apply {
            text = "Open Bluetooth Settings"
            setOnClickListener { openBluetoothSettings() }
        }
        val notificationSettings = Button(this).apply {
            text = "Open Notification Settings"
            setOnClickListener { openNotificationSettings() }
        }
        val batterySettings = Button(this).apply {
            text = "Open Battery / Unrestricted Settings"
            setOnClickListener { openBatteryOptimizationSettings() }
        }
        val rotateToken = Button(this).apply {
            text = "Regenerate Pairing Token"
            setOnClickListener { refreshToken() }
        }
        val stop = Button(this).apply {
            text = "Stop Bridge"
            setOnClickListener { stopBridge() }
        }

        layout.addView(status)
        layout.addView(guidance)
        layout.addView(qrView)
        layout.addView(pairingText)
        layout.addView(usb)
        layout.addView(wifi)
        layout.addView(pan)
        layout.addView(pairBluetooth)
        layout.addView(bluetoothBridge)
        layout.addView(directStart)
        layout.addView(directRefresh)
        layout.addView(directStop)
        layout.addView(copyPayload)
        layout.addView(usbPairingInject)
        layout.addView(wifiSettings)
        layout.addView(btSettings)
        layout.addView(notificationSettings)
        layout.addView(batterySettings)
        layout.addView(rotateToken)
        layout.addView(stop)
        scroll.addView(layout)
        setContentView(scroll)
        val filter = IntentFilter(ProxyForegroundService.ACTION_STATE)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(stateReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        else @Suppress("UnspecifiedRegisterReceiverFlag") registerReceiver(stateReceiver, filter)
        updatePairingViews(BridgeMode.WIFI)
    }

    override fun onStop() {
        bluetoothPairing.stop()
        super.onStop()
    }

    override fun onDestroy() {
        unregisterReceiver(stateReceiver)
        direct.dispose()
        super.onDestroy()
    }

    private fun prepareUsbPairingInject() {
        val payload = PairingPayload.build(this, BridgeMode.WIFI, directHost)
        status.text = "USB pairing inject is ready. On Windows, click Inject from USB phone in the Pairing tab to pull this payload over ADB.\nToken: ${TokenStore.getOrCreateToken(this)}"
        pairingText.text = "Pairing payload:\n$payload"
        qrView.setImageBitmap(QrCodeRenderer.render(payload))
    }

    private fun startBridge(mode: BridgeMode) {
        val token = TokenStore.getOrCreateToken(this)
        val intent = Intent(this, ProxyForegroundService::class.java).apply {
            action = ProxyForegroundService.ACTION_START
            putExtra(ProxyForegroundService.EXTRA_MODE, mode.name)
            putExtra(ProxyForegroundService.EXTRA_TOKEN, token)
        }
        startForegroundService(intent)
        status.text = "Starting bridge: $mode…"
        updatePairingViews(if (mode == BridgeMode.BLUETOOTH_PAN) BridgeMode.BLUETOOTH_PAN else BridgeMode.WIFI)
    }

    private fun stopBridge() {
        val intent = Intent(this, ProxyForegroundService::class.java).apply {
            action = ProxyForegroundService.ACTION_STOP
        }
        startService(intent)
        status.text = "Bridge stopped.\nToken: ${TokenStore.getOrCreateToken(this)}"
        updatePairingViews(BridgeMode.WIFI)
    }

    private fun refreshToken() {
        val token = TokenStore.rotateToken(this)
        status.text = "Pairing token regenerated.\nRestart Wi-Fi or Bluetooth/PAN bridge before using the new token.\nToken: $token"
        updatePairingViews(BridgeMode.WIFI)
    }

    private fun updatePairingViews(mode: BridgeMode) {
        val payload = PairingPayload.build(this, mode, directHost)
        pairingText.text = "Pairing payload:\n$payload"
        qrView.setImageBitmap(QrCodeRenderer.render(payload))
    }

    private fun copyPairingPayload() {
        val payload = PairingPayload.build(this, BridgeMode.WIFI, directHost)
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("TetherBridge pairing", payload))
        status.text = "Pairing payload copied. Paste it into the Windows Pairing box.\nToken: ${TokenStore.getOrCreateToken(this)}"
        updatePairingViews(BridgeMode.WIFI)
    }

    private fun openWifiSettings() {
        startActivity(Intent(Settings.ACTION_WIFI_SETTINGS))
    }

    private fun openBluetoothSettings() {
        startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS))
    }

    private fun openNotificationSettings() {
        val intent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
            }
        } else {
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))
        }
        startActivity(intent)
    }

    private fun openBatteryOptimizationSettings() {
        val packageUri = Uri.parse("package:$packageName")
        val requestIntent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, packageUri)
        } else {
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri)
        }
        try {
            startActivity(requestIntent)
        } catch (_: Exception) {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri))
        }
    }

    private fun requestBluetoothStart() {
        if (Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
            startBridge(BridgeMode.BLUETOOTH_RFCOMM)
        else requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), REQUEST_BLUETOOTH)
    }

    private fun requestWifiDirectStart() {
        val permission = if (Build.VERSION.SDK_INT >= 33) Manifest.permission.NEARBY_WIFI_DEVICES else Manifest.permission.ACCESS_FINE_LOCATION
        if (checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) direct.create()
        else requestPermissions(arrayOf(permission), REQUEST_WIFI_DIRECT)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_BLUETOOTH) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startBridge(BridgeMode.BLUETOOTH_RFCOMM)
            else status.text = "Bluetooth Bridge needs nearby-device permission."
        }
        if (requestCode == REQUEST_BLUETOOTH_PAIR) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) bluetoothPairing.start()
            else status.text = "Bluetooth pairing needs nearby-device permission."
        }
        if (requestCode == REQUEST_WIFI_DIRECT) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) direct.create()
            else status.text = "Wi-Fi Direct needs the requested nearby-device permission."
        }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS)
    }

    companion object {
        private const val REQUEST_NOTIFICATIONS = 6201
        private const val REQUEST_WIFI_DIRECT = 6202
        private const val REQUEST_BLUETOOTH = 6203
        private const val REQUEST_BLUETOOTH_PAIR = 6204
    }
}
