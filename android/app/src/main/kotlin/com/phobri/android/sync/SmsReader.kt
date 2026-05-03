package com.phobri.android.sync

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Telephony
import com.phobri.android.model.SmsMessage
import com.phobri.android.model.SmsType

/**
 * Reads SMS messages from the device using ContentResolver.
 */
class SmsReader(private val context: Context) {

    private val contentResolver: ContentResolver = context.contentResolver

    companion object {
        private val SMS_INBOX_URI: Uri = Uri.parse("content://sms/inbox")
        private val SMS_SENT_URI: Uri = Uri.parse("content://sms/sent")
        private val SMS_DRAFT_URI: Uri = Uri.parse("content://sms/draft")

        private val PROJECTION = arrayOf(
            "_id", "thread_id", "address", "person", "body", "date", "type", "read"
        )
    }

    /**
     * Query SMS content at a given URI leveraging Bundle args (API 26+)
     * for proper SQL LIMIT support, falling back to the legacy sortOrder hack
     * on older devices.
     */
    private fun querySmsUri(uri: Uri, selection: String?, selectionArgs: Array<String>?, sortOrder: String, limit: Int) =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val queryArgs = Bundle().apply {
                if (selection != null) {
                    putString(ContentResolver.QUERY_ARG_SQL_SELECTION, selection)
                }
                if (selectionArgs != null) {
                    putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, selectionArgs)
                }
                putString(ContentResolver.QUERY_ARG_SQL_SORT_ORDER, sortOrder)
                putInt(ContentResolver.QUERY_ARG_SQL_LIMIT, limit)
            }
            contentResolver.query(uri, PROJECTION, queryArgs, null)
        } else {
            contentResolver.query(uri, PROJECTION, selection, selectionArgs, "$sortOrder LIMIT $limit")
        }

    /**
     * Read SMS messages, optionally filtered by timestamp.
     * @param after Only return messages after this timestamp (millis).
     * @param limit Maximum number of messages to return.
     * @return List of SMS messages sorted by date descending.
     */
    fun readMessages(after: Long? = null, limit: Int = 100): List<SmsMessage> {
        val messages = mutableListOf<SmsMessage>()

        val selection = if (after != null) "date > ?" else null
        val selectionArgs = if (after != null) arrayOf(after.toString()) else null
        val sortOrder = "date DESC"

        listOf(SMS_INBOX_URI, SMS_SENT_URI, SMS_DRAFT_URI).forEach { uri ->
            querySmsUri(uri, selection, selectionArgs, sortOrder, limit)
                ?.use { cursor ->
                    while (cursor.moveToNext()) {
                        messages.add(cursorToSmsMessage(cursor))
                    }
                }
        }

        // Sort all combined results by date descending
        return messages.sortedByDescending { it.date }
    }

    /**
     * Read the most recent N messages without filtering.
     */
    fun readRecentMessages(count: Int = 50): List<SmsMessage> {
        return readMessages(after = null, limit = count)
    }

    /**
     * Read messages from a specific sender.
     */
    fun readConversation(address: String, limit: Int = 100): List<SmsMessage> {
        val messages = mutableListOf<SmsMessage>()
        val selection = "address = ?"
        val selectionArgs = arrayOf(address)
        val sortOrder = "date DESC"

        listOf(SMS_INBOX_URI, SMS_SENT_URI, SMS_DRAFT_URI).forEach { uri ->
            querySmsUri(uri, selection, selectionArgs, sortOrder, limit)
                ?.use { cursor ->
                    while (cursor.moveToNext()) {
                        messages.add(cursorToSmsMessage(cursor))
                    }
                }
        }

        return messages.sortedByDescending { it.date }
    }

    private fun cursorToSmsMessage(cursor: android.database.Cursor): SmsMessage {
        val typeInt = cursor.getInt(cursor.getColumnIndexOrThrow("type"))
        val type = when (typeInt) {
            Telephony.TextBasedSmsColumns.MESSAGE_TYPE_INBOX -> SmsType.INBOX
            Telephony.TextBasedSmsColumns.MESSAGE_TYPE_SENT -> SmsType.SENT
            Telephony.TextBasedSmsColumns.MESSAGE_TYPE_DRAFT -> SmsType.DRAFT
            Telephony.TextBasedSmsColumns.MESSAGE_TYPE_OUTBOX -> SmsType.OUTBOX
            Telephony.TextBasedSmsColumns.MESSAGE_TYPE_FAILED -> SmsType.FAILED
            else -> SmsType.INBOX
        }

        return SmsMessage(
            id = cursor.getLong(cursor.getColumnIndexOrThrow("_id")),
            threadId = cursor.getLong(cursor.getColumnIndexOrThrow("thread_id")),
            address = cursor.getString(cursor.getColumnIndexOrThrow("address")) ?: "",
            contactName = cursor.getString(cursor.getColumnIndexOrThrow("person")),
            body = cursor.getString(cursor.getColumnIndexOrThrow("body")) ?: "",
            date = cursor.getLong(cursor.getColumnIndexOrThrow("date")),
            type = type,
            read = cursor.getInt(cursor.getColumnIndexOrThrow("read")) == 1
        )
    }
}
