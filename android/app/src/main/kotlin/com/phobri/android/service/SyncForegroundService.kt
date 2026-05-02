package com.phobri.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import com.phobri.android.MainActivity
import com.phobri.android.model.*
import com.phobri.android.pairing.PairingManager
import com.phobri.android.sync.*
import kotlinx.coroutines.*
import kotlinx.serialization.json.*
import java.net.DatagramPacket
import java.net.DatagramSocket

/**
 * Foreground service that manages the WebSocket connection to the desktop
 * and listens for UDP wake packets.
 */
class SyncForegroundService : Service() {

    companion object {
        private const val TAG = "PhobriSync"
        private const val NOTIFICATION_ID = 1001
        private const val CHANNEL_ID = "phobri_sync"
        private const val UDP_WAKE_PORT = 9876

        // Actions
        const val ACTION_START = "com.phobri.android.action.START_SYNC"
        const val ACTION_STOP = "com.phobri.android.action.STOP_SYNC"

        // Extras
        const val EXTRA_DESKTOP_HOST = "desktop_host"
        const val EXTRA_DESKTOP_PORT = "desktop_port"
    }

    private val serviceScope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private lateinit var pairingManager: PairingManager
    private lateinit var wsClient: PhobriWebSocketClient
    private lateinit var smsReader: SmsReader
    private lateinit var callLogReader: CallLogReader
    private var smsObserver: SmsObserver? = null
    private var callObserver: CallObserver? = null

    private var isRunning = false
    private var udpSocket: DatagramSocket? = null
    private val json = Json { ignoreUnknownKeys = true }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        pairingManager = PairingManager(this)
        wsClient = PhobriWebSocketClient(pairingManager)
        smsReader = SmsReader(this)
        callLogReader = CallLogReader(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                val host = intent.getStringExtra(EXTRA_DESKTOP_HOST)
                    ?: pairingManager.desktopHost ?: return START_NOT_STICKY
                val port = intent.getIntExtra(EXTRA_DESKTOP_PORT, pairingManager.desktopPort)

                startSync(host, port)
            }
            ACTION_STOP -> {
                stopSync()
                stopSelf()
            }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        stopSync()
        serviceScope.cancel()
        super.onDestroy()
    }

    // --- Start / Stop ---

    private fun startSync(host: String, port: Int) {
        if (isRunning) return
        isRunning = true

        val token = pairingManager.pairingToken ?: run {
            Log.w(TAG, "Cannot start sync: not paired")
            stopSelf()
            return
        }

        startForeground(NOTIFICATION_ID, buildNotification("Connecting..."))

        // Start UDP wake listener
        startUdpWakeListener()

        // Start observers for real-time SMS and call detection
        setupObservers()

        // Start WebSocket connection
        serviceScope.launch {
            while (isActive && isRunning) {
                try {
                    val url = "wss://$host:$port/sync"
                    Log.d(TAG, "Connecting to $url")

                    // Initial sync after connecting
                    wsClient.connect(url, token)

                    if (wsClient.connectionState.value) {
                        // Perform challenge-response to verify server knows the password
                        val authOk = wsClient.performChallengeResponse()
                        if (!authOk) {
                            Log.w(TAG, "Challenge-response failed — server may not have the correct password")
                            wsClient.disconnect()
                            updateNotification("Authentication failed — check server password")
                            delay(30_000) // Longer delay on auth failure
                            continue
                        }

                        updateNotification("Connected to desktop")
                        performInitialSync()
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Connection error", e)
                    updateNotification("Disconnected - retrying...")
                }

                // Wait before reconnecting
                delay(5_000)
            }
        }
    }

    private fun stopSync() {
        isRunning = false
        serviceScope.launch {
            wsClient.disconnect()
        }
        smsObserver?.stop()
        callObserver?.stop()
        stopUdpWakeListener()
    }

    // --- Initial Sync ---

    private suspend fun performInitialSync() {
        // Sync SMS
        val smsMessages = smsReader.readRecentMessages(500)
        if (smsMessages.isNotEmpty()) {
            wsClient.sendSmsSync(smsMessages)
            Log.d(TAG, "Synced ${smsMessages.size} SMS messages")
        }

        // Sync Call Log
        val calls = callLogReader.readRecentCalls(200)
        if (calls.isNotEmpty()) {
            wsClient.sendCallSync(calls)
            Log.d(TAG, "Synced ${calls.size} call log entries")
        }

        // Listen for incoming messages (sync requests, send commands)
        launchIncomingMessageHandler()
    }

    // --- Observers ---

    private fun setupObservers() {
        smsObserver = SmsObserver(this) {
            serviceScope.launch {
                if (wsClient.connectionState.value) {
                    val latest = smsReader.readRecentMessages(1)
                    latest.firstOrNull()?.let { sms ->
                        wsClient.pushNewSms(sms)
                        Log.d(TAG, "Pushed new SMS from ${sms.address}")
                    }
                }
            }
        }
        smsObserver?.start()

        callObserver = CallObserver(this)
        callObserver?.start {
            serviceScope.launch {
                if (wsClient.connectionState.value) {
                    val latest = callLogReader.readRecentCalls(1)
                    latest.firstOrNull()?.let { call ->
                        wsClient.pushNewCall(call)
                        Log.d(TAG, "Pushed new call: ${call.number}")
                    }
                }
            }
        }
    }

    // --- UDP Wake Listener ---

    private fun startUdpWakeListener() {
        serviceScope.launch {
            try {
                udpSocket = DatagramSocket(UDP_WAKE_PORT)
                val buffer = ByteArray(4)

                while (isActive && isRunning) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    udpSocket?.receive(packet)

                    val message = String(packet.data, 0, packet.length)
                    if (message == "WAKE") {
                        Log.d(TAG, "Received WAKE packet from ${packet.address.hostAddress}")
                        // Trigger reconnection if not connected
                        if (!wsClient.connectionState.value) {
                            updateNotification("Woke up - connecting...")
                        }
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "UDP listener error", e)
            }
        }
    }

    private fun stopUdpWakeListener() {
        try {
            udpSocket?.close()
        } catch (_: Exception) { }
        udpSocket = null
    }

    // --- Incoming Message Handler ---

    private fun launchIncomingMessageHandler() {
        serviceScope.launch {
            wsClient.incomingMessages.collect { message ->
                when (message.type) {
                    MessageType.REQUEST -> handleRequest(message)
                    MessageType.RESPONSE -> Log.d(TAG, "Response: ${message.action}")
                    else -> { /* ignore other types */ }
                }
            }
        }
    }

    private suspend fun handleRequest(message: ProtocolMessage) {
        when (message.action) {
            "sms.sync.request" -> {
                val after = message.payload?.jsonObject?.get("after")?.jsonPrimitive?.longOrNull
                val limit = message.payload?.jsonObject?.get("limit")?.jsonPrimitive?.intOrNull ?: 100
                val messages = smsReader.readMessages(after = after, limit = limit)
                wsClient.sendSmsSync(messages)
            }
            "call.sync.request" -> {
                val after = message.payload?.jsonObject?.get("after")?.jsonPrimitive?.longOrNull
                val limit = message.payload?.jsonObject?.get("limit")?.jsonPrimitive?.intOrNull ?: 100
                val calls = callLogReader.readCallLog(after = after, limit = limit)
                wsClient.sendCallSync(calls)
            }
            "sms.send" -> {
                // SMS sending requires SEND_SMS permission and SmsManager
                // Implementation depends on Android permissions
                Log.d(TAG, "SMS send requested (not implemented)")
            }
        }
    }

    // --- Notifications ---

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Phobri Sync Service",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Shows when Phobri sync service is running"
            }
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(text: String): Notification {
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Phobri Sync")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setOngoing(true)
            .setContentIntent(pendingIntent)
            .build()
    }

    private fun updateNotification(text: String) {
        val notification = buildNotification(text)
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, notification)
    }
}
