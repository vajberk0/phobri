package com.phobri.android.sync

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.telephony.PhoneStateListener
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat

/**
 * Listens for phone call state changes and triggers callbacks for new call log entries.
 * Note: Android doesn't have a direct ContentObserver for call log changes,
 * so we poll periodically when triggered by phone state changes.
 */
class CallObserver(
    private val context: Context
) {
    private var phoneStateListener: PhoneStateListener? = null
    private var lastKnownCallState = TelephonyManager.CALL_STATE_IDLE
    private var onCallEnded: (() -> Unit)? = null

    /**
     * Start listening for call state changes.
     * @param onNewCall Called when a call ends (potential new call log entry).
     */
    fun start(onNewCall: () -> Unit) {
        this.onCallEnded = onNewCall

        if (ContextCompat.checkSelfPermission(context, Manifest.permission.READ_PHONE_STATE)
            != PackageManager.PERMISSION_GRANTED) {
            return
        }

        val telephonyManager = context.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager
            ?: return

        phoneStateListener = object : PhoneStateListener() {
            override fun onCallStateChanged(state: Int, phoneNumber: String?) {
                // Detect call ended (transition from ringing/active to idle)
                if (lastKnownCallState != TelephonyManager.CALL_STATE_IDLE &&
                    state == TelephonyManager.CALL_STATE_IDLE) {
                    onCallEnded?.invoke()
                }
                lastKnownCallState = state
            }
        }

        telephonyManager.listen(
            phoneStateListener,
            PhoneStateListener.LISTEN_CALL_STATE
        )
    }

    /**
     * Stop listening for call state changes.
     */
    fun stop() {
        phoneStateListener?.let { listener ->
            val telephonyManager = context.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager
            telephonyManager?.listen(listener, PhoneStateListener.LISTEN_NONE)
        }
        phoneStateListener = null
    }
}
