package com.phobri.android.sync

import android.content.ContentResolver
import android.content.Context
import android.provider.CallLog
import com.phobri.android.model.CallLogEntry
import com.phobri.android.model.CallType

/**
 * Reads call log entries from the device using ContentResolver.
 */
class CallLogReader(private val context: Context) {

    private val contentResolver: ContentResolver = context.contentResolver

    companion object {
        private val PROJECTION = arrayOf(
            CallLog.Calls._ID,
            CallLog.Calls.NUMBER,
            CallLog.Calls.CACHED_NAME,
            CallLog.Calls.DATE,
            CallLog.Calls.DURATION,
            CallLog.Calls.TYPE
        )
    }

    /**
     * Read call log entries, optionally filtered by timestamp.
     * @param after Only return entries after this timestamp (millis).
     * @param limit Maximum number of entries to return.
     * @return List of call log entries sorted by date descending.
     */
    fun readCallLog(after: Long? = null, limit: Int = 100): List<CallLogEntry> {
        val calls = mutableListOf<CallLogEntry>()

        val selection = if (after != null) "${CallLog.Calls.DATE} > ?" else null
        val selectionArgs = if (after != null) arrayOf(after.toString()) else null
        val sortOrder = "${CallLog.Calls.DATE} DESC LIMIT $limit"

        try {
            contentResolver.query(
                CallLog.Calls.CONTENT_URI,
                PROJECTION,
                selection,
                selectionArgs,
                sortOrder
            )?.use { cursor ->
                while (cursor.moveToNext()) {
                    try {
                        calls.add(cursorToCallLogEntry(cursor))
                    } catch (e: Exception) {
                        android.util.Log.w("CallLogReader", "Failed to parse call log row", e)
                    }
                }
            }
        } catch (e: SecurityException) {
            android.util.Log.w("CallLogReader", "Permission denied reading call log: ${e.message}")
        } catch (e: Exception) {
            android.util.Log.e("CallLogReader", "Failed to read call log: ${e.message}", e)
        }

        return calls
    }

    /**
     * Read the most recent call log entries without filtering.
     */
    fun readRecentCalls(count: Int = 50): List<CallLogEntry> {
        return readCallLog(after = null, limit = count)
    }

    private fun cursorToCallLogEntry(cursor: android.database.Cursor): CallLogEntry {
        val typeInt = cursor.getInt(cursor.getColumnIndexOrThrow(CallLog.Calls.TYPE))
        val type = when (typeInt) {
            CallLog.Calls.INCOMING_TYPE -> CallType.INCOMING
            CallLog.Calls.OUTGOING_TYPE -> CallType.OUTGOING
            CallLog.Calls.MISSED_TYPE -> CallType.MISSED
            // REJECTED_TYPE and BLOCKED_TYPE are API 24+ constants;
            // some OEMs may not define them, so use raw ints as fallback
            5 -> CallType.REJECTED
            6 -> CallType.BLOCKED
            else -> CallType.INCOMING
        }

        return CallLogEntry(
            id = cursor.getLong(cursor.getColumnIndexOrThrow(CallLog.Calls._ID)),
            number = cursor.getString(cursor.getColumnIndexOrThrow(CallLog.Calls.NUMBER)) ?: "",
            contactName = cursor.getString(cursor.getColumnIndexOrThrow(CallLog.Calls.CACHED_NAME)),
            date = cursor.getLong(cursor.getColumnIndexOrThrow(CallLog.Calls.DATE)),
            duration = cursor.getLong(cursor.getColumnIndexOrThrow(CallLog.Calls.DURATION)),
            type = type
        )
    }
}
