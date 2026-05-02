package com.phobri.android.sync

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.Telephony

/**
 * ContentObserver that monitors the SMS content provider for changes.
 * When a new SMS arrives, it triggers a callback.
 */
class SmsObserver(
    private val context: Context,
    private val onNewSms: () -> Unit
) : ContentObserver(Handler(Looper.getMainLooper())) {

    companion object {
        private val SMS_CONTENT_URI: Uri = Uri.parse("content://sms")
    }

    private var isRegistered = false

    /**
     * Start observing SMS changes.
     */
    fun start() {
        if (isRegistered) return
        context.contentResolver.registerContentObserver(
            SMS_CONTENT_URI,
            notifyForDescendants = true,
            observer = this
        )
        isRegistered = true
    }

    /**
     * Stop observing SMS changes.
     */
    fun stop() {
        if (!isRegistered) return
        context.contentResolver.unregisterContentObserver(this)
        isRegistered = false
    }

    override fun onChange(selfChange: Boolean) {
        super.onChange(selfChange)
        onNewSms()
    }
}
