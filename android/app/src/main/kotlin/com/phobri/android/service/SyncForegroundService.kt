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
import com.google.firebase.messaging.FirebaseMessaging
import com.phobri.android.model.*
import com.phobri.android.pairing.PairingManager
import com.phobri.android.sync.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.flow.first
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

        const val ACTION_START = "com.phobri.android.action.START_SYNC"
        const val ACTION_STOP = "com.phobri.android.action.STOP_SYNC"
        const val ACTION_FCM_TOKEN = "com.phobri.android.action.FCM_TOKEN"
        const val EXTRA_DESKTOP_HOST = "desktop_host"
        const val EXTRA_DESKTOP_PORT = "desktop_port"

        // Exposed state for the UI
        private val _isServiceRunning = MutableStateFlow(false)
        val isServiceRunning: StateFlow<Boolean> = _isServiceRunning.asStateFlow()

        private val _connectionState = MutableStateFlow(false)
        val connectionState: StateFlow<Boolean> = _connectionState.asStateFlow()

        private val _sentCount = MutableStateFlow(0)
        val sentCount: StateFlow<Int> = _sentCount.asStateFlow()

        private val _receivedCount = MutableStateFlow(0)
        val receivedCount: StateFlow<Int> = _receivedCount.asStateFlow()

        private val _lastError = MutableStateFlow<String?>(null)
        val lastError: StateFlow<String?> = _lastError.asStateFlow()

        // Merged event log from the WS client
        private val _eventLog = MutableStateFlow<List<ClientEvent>>(emptyList())
        val eventLog: StateFlow<List<ClientEvent>> = _eventLog.asStateFlow()
    }

    private val serviceScope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private lateinit var pairingManager: PairingManager
    private var wsClient: PhobriWebSocketClient? = null
    private lateinit var smsReader: SmsReader
    private lateinit var callLogReader: CallLogReader
    private var smsObserver: SmsObserver? = null
    private var callObserver: CallObserver? = null

    private var isRunning = false
    private var udpSocket: DatagramSocket? = null
    private val json = Json { ignoreUnknownKeys = true }

    // Collection jobs
    private var stateCollectionJob: Job? = null
    private var mainLoopJob: Job? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        pairingManager = PairingManager(this)
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
            ACTION_FCM_TOKEN -> {
                // Token forwarded from FcmReceiver; if connected, send it now
                val token = intent.getStringExtra("fcm_token")
                if (token != null && isRunning) {
                    serviceScope.launch {
                        wsClient?.sendFcmToken(token)
                    }
                }
            }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        stopSync()
        serviceScope.cancel()
        _isServiceRunning.value = false
        super.onDestroy()
    }

    private fun startSync(host: String, port: Int) {
        if (isRunning) return
        isRunning = true
        _isServiceRunning.value = true

        // Create a fresh WS client
        wsClient = PhobriWebSocketClient(pairingManager)

        // Collect state from the client and forward to companion StateFlows
        val client = wsClient!!
        stateCollectionJob = serviceScope.launch {
            launch {
                client.connectionState.collect { connected ->
                    val wasConnected = _connectionState.value
                    _connectionState.value = connected
                    // Update notification immediately when connection state changes
                    if (!connected && wasConnected) {
                        Log.w(TAG, "Connection lost — updating notification")
                        updateNotification("Disconnected - retrying...")
                    } else if (connected && !wasConnected) {
                        updateNotification("Connected to desktop")
                    }
                }
            }
            launch {
                client.sentCount.collect { _sentCount.value = it }
            }
            launch {
                client.receivedCount.collect { _receivedCount.value = it }
            }
            launch {
                client.lastError.collect { _lastError.value = it }
            }
            launch {
                client.eventLog.collect { _eventLog.value = it }
            }
        }

        val token = pairingManager.pairingToken ?: run {
            Log.w(TAG, "Cannot start sync: not paired")
            _lastError.value = "Not paired"
            stopSelf()
            return
        }

        startForeground(NOTIFICATION_ID, buildNotification("Connecting..."))
        startUdpWakeListener()
        setupObservers()

        mainLoopJob = serviceScope.launch {
            while (isActive && isRunning) {
                try {
                    // Only connect if not already connected
                    if (!client.connectionState.value) {
                        updateNotification("Connecting...")
                        val url = "wss://$host:$port/sync"
                        Log.d(TAG, "Connecting to $url")

                        client.connect(url, token)

                        if (client.connectionState.value) {
                            updateNotification("Connected to desktop")

                            val authOk = client.performChallengeResponse()
                            if (!authOk) {
                                Log.w(TAG, "Challenge-response failed")
                                client.disconnect()
                                updateNotification("Auth failed - check server password")
                                delay(30_000)
                                continue
                            }

                            performInitialSync()

                            // Send FCM token so desktop can push-wake us
                            sendCurrentFcmToken(client)
                        } else {
                            Log.w(TAG, "connect() returned but connectionState is false")
                        }
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Connection error", e)
                    updateNotification("Disconnected - retrying...")
                }

                delay(3_000)
            }
            Log.d(TAG, "Main loop exited (isRunning=$isRunning)")
        }
    }

    private fun stopSync() {
        Log.d(TAG, "stopSync() called, isRunning=$isRunning")
        isRunning = false
        _isServiceRunning.value = false
        mainLoopJob?.cancel()
        stateCollectionJob?.cancel()
        serviceScope.launch {
            wsClient?.disconnect()
        }
        wsClient = null
        smsObserver?.stop()
        callObserver?.stop()
        stopUdpWakeListener()
        _connectionState.value = false
        updateNotification("Sync stopped")
    }

    /**
     * Get the current FCM registration token and send it to the desktop.
     * This enables the desktop to send push messages that wake the phone.
     */
    private suspend fun sendCurrentFcmToken(client: PhobriWebSocketClient) {
        try {
            val token = suspendCancellableCoroutine<String?> { cont ->
                FirebaseMessaging.getInstance().token
                    .addOnCompleteListener { task ->
                        if (task.isSuccessful) {
                            cont.resume(task.result, null)
                        } else {
                            // No FCM token available (e.g., no Google Play Services)
                            Log.w(TAG, "FCM token unavailable: ${task.exception?.message}")
                            cont.resume(null, null)
                        }
                    }
            }
            if (token != null) {
                client.sendFcmToken(token)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to get FCM token: ${e.message}")
        }
    }

    private suspend fun performInitialSync() {
        val client = wsClient ?: return
        val maxEntries = pairingManager.maxSyncEntries

        // Sync only recent messages to keep frames small and fast
        if (pairingManager.syncSmsEnabled) {
            val smsMessages = smsReader.readRecentMessages(maxEntries)
            if (smsMessages.isNotEmpty()) {
                client.sendSmsSync(smsMessages)
                Log.d(TAG, "Synced ${smsMessages.size} SMS messages")
            }
        } else {
            Log.d(TAG, "SMS sync disabled — skipping")
        }

        if (pairingManager.syncCallsEnabled) {
            val calls = callLogReader.readRecentCalls(maxEntries)
            if (calls.isNotEmpty()) {
                client.sendCallSync(calls)
                Log.d(TAG, "Synced ${calls.size} call log entries")
            } else {
                Log.d(TAG, "No call log entries found to sync (check READ_CALL_LOG permission)")
            }
        } else {
            Log.d(TAG, "Call log sync disabled — skipping")
        }

        launchIncomingMessageHandler()
    }

    private fun setupObservers() {
        val client = wsClient ?: return

        if (pairingManager.syncSmsEnabled) {
            smsObserver = SmsObserver(this) {
                serviceScope.launch {
                    if (client.connectionState.value) {
                        val latest = smsReader.readRecentMessages(1)
                        latest.firstOrNull()?.let { sms ->
                            client.pushNewSms(sms)
                        }
                    }
                }
            }
            smsObserver?.start()
        }

        if (pairingManager.syncCallsEnabled) {
            Log.d(TAG, "Setting up CallObserver...")
            callObserver = CallObserver(this)
            callObserver?.start {
                serviceScope.launch {
                    Log.d(TAG, "CallObserver triggered — connectionState=${client.connectionState.value}")
                    // Android writes call log entries asynchronously after a call ends;
                    // give the ContentProvider time to finish writing (500ms delay).
                    delay(500)
                    if (client.connectionState.value) {
                        val latest = callLogReader.readRecentCalls(1)
                        Log.d(TAG, "CallObserver read ${latest.size} recent calls")
                        latest.firstOrNull()?.let { call ->
                            Log.d(TAG, "Pushing new call: ${call.number} / ${call.type}")
                            client.pushNewCall(call)
                        }
                    } else {
                        Log.w(TAG, "CallObserver triggered but not connected — skipping push")
                    }
                }
            }
        } else {
            Log.d(TAG, "Call log sync disabled — skipping CallObserver setup")
        }
    }

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
                        if (wsClient?.connectionState?.value != true) {
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

    private fun launchIncomingMessageHandler() {
        val client = wsClient ?: return
        serviceScope.launch {
            client.incomingMessages.collect { message ->
                when (message.type) {
                    MessageType.REQUEST -> handleRequest(message, client)
                    MessageType.RESPONSE -> Log.d(TAG, "Response: ${message.action}")
                    else -> { }
                }
            }
        }
    }

    private suspend fun handleRequest(message: ProtocolMessage, client: PhobriWebSocketClient) {
        when (message.action) {
            "sms.sync.request" -> {
                if (!pairingManager.syncSmsEnabled) {
                    Log.d(TAG, "SMS sync disabled — ignoring sms.sync.request")
                    return
                }
                val after = message.payload?.jsonObject?.get("after")?.jsonPrimitive?.longOrNull
                val limit = message.payload?.jsonObject?.get("limit")?.jsonPrimitive?.intOrNull
                    ?: pairingManager.maxSyncEntries
                val messages = smsReader.readMessages(after = after, limit = limit)
                client.sendSmsSync(messages)
            }
            "call.sync.request" -> {
                if (!pairingManager.syncCallsEnabled) {
                    Log.d(TAG, "Call log sync disabled — ignoring call.sync.request")
                    return
                }
                val after = message.payload?.jsonObject?.get("after")?.jsonPrimitive?.longOrNull
                val limit = message.payload?.jsonObject?.get("limit")?.jsonPrimitive?.intOrNull
                    ?: pairingManager.maxSyncEntries
                val calls = callLogReader.readCallLog(after = after, limit = limit)
                client.sendCallSync(calls)
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
