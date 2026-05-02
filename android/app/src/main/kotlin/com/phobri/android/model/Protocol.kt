package com.phobri.android.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

/**
 * Protocol message for WebSocket communication with the desktop.
 */
@Serializable
data class ProtocolMessage(
    val type: MessageType = MessageType.REQUEST,
    val id: String? = null,
    val action: String = "",
    val payload: JsonElement? = null,
    val error: String? = null
)

@Serializable
enum class MessageType {
    REQUEST, RESPONSE, PUSH, ERROR
}

/**
 * Payload for sms.sync action.
 */
@Serializable
data class SmsSyncPayload(
    val messages: List<SmsMessage> = emptyList(),
    val hasMore: Boolean = false
)

/**
 * Payload for call.sync action.
 */
@Serializable
data class CallSyncPayload(
    val calls: List<CallLogEntry> = emptyList(),
    val hasMore: Boolean = false
)

/**
 * Payload for sms.send request.
 */
@Serializable
data class SendSmsRequest(
    val phoneNumbers: List<String>,
    val text: String
)

/**
 * Payload for sync requests.
 */
@Serializable
data class SyncRequest(
    val after: Long? = null,
    val limit: Int? = null
)

/**
 * Payload for ping/pong.
 */
@Serializable
data class PingPayload(
    val timestamp: Long
)
