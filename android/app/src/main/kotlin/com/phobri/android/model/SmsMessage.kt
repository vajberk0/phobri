package com.phobri.android.model

import kotlinx.serialization.Serializable

/**
 * SMS message synchronized with the desktop.
 */
@Serializable
data class SmsMessage(
    val id: Long = 0,
    val threadId: Long = 0,
    val address: String = "",
    val contactName: String? = null,
    val body: String = "",
    val date: Long = 0,
    val type: SmsType = SmsType.INBOX,
    val read: Boolean = true
) {
    /** Display-friendly name, falls back to address. */
    val displayName: String get() = contactName ?: address
}

@Serializable
enum class SmsType {
    INBOX, SENT, DRAFT, OUTBOX, FAILED
}

/**
 * Call log entry synchronized with the desktop.
 */
@Serializable
data class CallLogEntry(
    val id: Long = 0,
    val number: String = "",
    val contactName: String? = null,
    val date: Long = 0,
    val duration: Long = 0,
    val type: CallType = CallType.INCOMING
) {
    /** Display-friendly name, falls back to number. */
    val displayName: String get() = contactName ?: number

    /** Duration formatted as m:ss. */
    val durationFormatted: String get() {
        val minutes = duration / 60
        val seconds = duration % 60
        return "$minutes:${seconds.toString().padStart(2, '0')}"
    }
}

@Serializable
enum class CallType {
    INCOMING, OUTGOING, MISSED, REJECTED, BLOCKED
}
