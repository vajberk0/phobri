package com.phobri.android.sync

import com.phobri.android.model.*
import io.ktor.client.*
import io.ktor.client.plugins.websocket.*
import io.ktor.websocket.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.serialization.json.*

/**
 * WebSocket client that connects to the desktop server.
 * Handles reconnection, message sending/receiving, and protocol operations.
 */
class PhobriWebSocketClient(
    private val client: HttpClient = defaultClient()
) {
    private var session: WebSocketSession? = null
    private val _connectionState = MutableStateFlow(false)
    val connectionState: StateFlow<Boolean> = _connectionState.asStateFlow()

    private val _incomingMessages = MutableSharedFlow<ProtocolMessage>(replay = 0)
    val incomingMessages: SharedFlow<ProtocolMessage> = _incomingMessages.asSharedFlow()

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        fun defaultClient() = HttpClient {
            install(WebSockets) {
                pingIntervalMillis = 30_000
            }
        }
    }

    /**
     * Connect to the desktop server.
     * @param url WebSocket URL (e.g., wss://192.168.1.10:8765/sync).
     * @param token Pairing token for authentication.
     */
    suspend fun connect(url: String, token: String) {
        try {
            client.webSocket(url) {
                // Send auth token as first message
                send(Frame.Text("""{"type":"REQUEST","action":"pair.init","payload":{"token":"$token"}}"""))

                session = this
                _connectionState.value = true

                // Listen for incoming messages
                for (frame in incoming) {
                    if (frame is Frame.Text) {
                        val text = frame.readText()
                        try {
                            val msg = json.decodeFromString<ProtocolMessage>(text)
                            _incomingMessages.emit(msg)
                        } catch (e: Exception) {
                            // Skip malformed messages
                        }
                    }
                }
            }
        } catch (e: Exception) {
            // Connection failed or lost
        } finally {
            session = null
            _connectionState.value = false
        }
    }

    /**
     * Send a protocol message to the desktop.
     */
    suspend fun sendMessage(message: ProtocolMessage) {
        session?.let { ws ->
            val text = json.encodeToString(ProtocolMessage.serializer(), message)
            ws.send(Frame.Text(text))
        }
    }

    /**
     * Push a new SMS notification to the desktop.
     */
    suspend fun pushNewSms(sms: SmsMessage) {
        val payload = json.encodeToJsonElement(SmsMessage.serializer(), sms)
        val msg = ProtocolMessage(
            type = MessageType.PUSH,
            action = "sms.new",
            payload = payload
        )
        sendMessage(msg)
    }

    /**
     * Push a new call log entry to the desktop.
     */
    suspend fun pushNewCall(call: CallLogEntry) {
        val payload = json.encodeToJsonElement(CallLogEntry.serializer(), call)
        val msg = ProtocolMessage(
            type = MessageType.PUSH,
            action = "call.new",
            payload = payload
        )
        sendMessage(msg)
    }

    /**
     * Send a batch of SMS messages for sync.
     */
    suspend fun sendSmsSync(messages: List<SmsMessage>, hasMore: Boolean = false) {
        val syncPayload = SmsSyncPayload(messages = messages, hasMore = hasMore)
        val payload = json.encodeToJsonElement(SmsSyncPayload.serializer(), syncPayload)
        val msg = ProtocolMessage(
            type = MessageType.PUSH,
            action = "sms.sync",
            payload = payload
        )
        sendMessage(msg)
    }

    /**
     * Send a batch of call log entries for sync.
     */
    suspend fun sendCallSync(calls: List<CallLogEntry>, hasMore: Boolean = false) {
        val syncPayload = CallSyncPayload(calls = calls, hasMore = hasMore)
        val payload = json.encodeToJsonElement(CallSyncPayload.serializer(), syncPayload)
        val msg = ProtocolMessage(
            type = MessageType.PUSH,
            action = "call.sync",
            payload = payload
        )
        sendMessage(msg)
    }

    /**
     * Disconnect from the server.
     */
    suspend fun disconnect() {
        try {
            session?.close()
        } catch (_: Exception) { }
        session = null
        _connectionState.value = false
    }
}
