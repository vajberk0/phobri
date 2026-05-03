package com.phobri.android.service

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.util.Log
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.phobri.android.MainActivity
import com.phobri.android.pairing.PairingManager

/**
 * Receives FCM messages from the desktop server.
 *
 * When the desktop wants the phone to connect (e.g., server IP changed,
 * phone stopped syncing, or UDP wake failed), it sends a high-priority
 * FCM data message. This receiver parses the message, updates the stored
 * desktop host if needed, and triggers a reconnect.
 *
 * FCM data message payload (all string key-value pairs):
 *   type        = "wake"
 *   serverHost  = "192.168.1.5" (or Tailscale IP / hostname)
 *   serverPort  = "8765"
 */
class FcmReceiver : FirebaseMessagingService() {

    companion object {
        private const val TAG = "PhobriFCM"
        private const val PREFS_TOKEN = "phobri_fcm_token"

        /**
         * Get the persisted FCM token (may be stale if onNewToken hasn't
         * been called). SyncForegroundService should call this as a fallback
         * if FirebaseMessaging.token() is unavailable.
         */
        fun getStoredToken(context: Context): String? {
            return context.getSharedPreferences(PREFS_TOKEN, Context.MODE_PRIVATE)
                .getString("token", null)
        }
    }

    /**
     * Called when a new FCM token is generated.
     * Store it so the sync service can send it to the desktop.
     */
    override fun onNewToken(token: String) {
        super.onNewToken(token)
        Log.d(TAG, "New FCM token: ${token.take(16)}...")

        // Persist the token
        getSharedPreferences(PREFS_TOKEN, Context.MODE_PRIVATE)
            .edit()
            .putString("token", token)
            .apply()

        // Forward to the active sync service (it sends via WS)
        sendTokenToService(token)
    }

    /**
     * Called when a push message is received.
     * Even if the app is in the background or stopped, this is invoked
     * for high-priority data messages.
     */
    override fun onMessageReceived(message: RemoteMessage) {
        super.onMessageReceived(message)
        Log.d(TAG, "FCM message received: from=${message.from}, data=${message.data}")

        val data = message.data
        val type = data["type"] ?: return
        val serverHost = data["serverHost"]
        val serverPort = data["serverPort"]?.toIntOrNull() ?: 8765

        when (type) {
            "wake" -> {
                handleWake(serverHost, serverPort)
            }
            else -> {
                Log.d(TAG, "Unknown FCM message type: $type")
            }
        }
    }

    /**
     * Handle a wake message from the desktop.
     * Updates the stored host if it changed, then starts the sync service.
     */
    private fun handleWake(serverHost: String?, serverPort: Int) {
        if (serverHost.isNullOrBlank()) {
            Log.w(TAG, "Wake message missing serverHost")
            return
        }

        Log.d(TAG, "Wake received: host=$serverHost port=$serverPort")

        val pairingManager = PairingManager(this)

        // Only act if we're paired
        if (!pairingManager.isPaired) {
            Log.w(TAG, "Ignoring wake — not paired")
            return
        }

        // Update the stored host if it changed (e.g., Tailscale IP rotation)
        val oldHost = pairingManager.desktopHost
        if (oldHost != serverHost) {
            Log.d(TAG, "Desktop host changed: $oldHost → $serverHost")
            pairingManager.updateHost(serverHost)
        }

        // Start the sync service (or restart if already running)
        val intent = Intent(this, SyncForegroundService::class.java).apply {
            action = SyncForegroundService.ACTION_START
            putExtra(SyncForegroundService.EXTRA_DESKTOP_HOST, serverHost)
            putExtra(SyncForegroundService.EXTRA_DESKTOP_PORT, serverPort)
        }

        try {
            startForegroundService(intent)
            Log.d(TAG, "SyncForegroundService started")
        } catch (e: Exception) {
            // On Android 12+, background start restrictions may prevent
            // starting a foreground service from the background.
            // FCM high-priority messages get an exemption for ~10 seconds,
            // but if something goes wrong, log it.
            Log.e(TAG, "Failed to start SyncForegroundService", e)
        }
    }

    /**
     * Forward the current FCM token to the sync service so it can send it
     * to the desktop. Called both from onNewToken() and on demand from
     * SyncForegroundService.
     */
    private fun sendTokenToService(token: String) {
        val intent = Intent(this, SyncForegroundService::class.java).apply {
            action = SyncForegroundService.ACTION_FCM_TOKEN
            putExtra("fcm_token", token)
        }
        try {
            startService(intent)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to forward token to service", e)
        }
    }
}
