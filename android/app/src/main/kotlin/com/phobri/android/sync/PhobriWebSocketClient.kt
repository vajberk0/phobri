package com.phobri.android.sync

import android.util.Base64
import android.util.Log
import com.phobri.android.model.*
import com.phobri.android.pairing.PairingManager
import io.ktor.client.*
import io.ktor.client.engine.okhttp.*
import io.ktor.client.plugins.websocket.*
import io.ktor.websocket.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.serialization.json.*
import java.security.cert.X509Certificate
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.X509TrustManager

/**
 * Events emitted by the WebSocket client for UI logging.
 */
data class ClientEvent(
    val timestamp: Long = System.currentTimeMillis(),
    val type: String,
    val detail: String
)

/**
 * WebSocket client that connects to the desktop server.
 * Handles reconnection, message sending/receiving, protocol operations,
 * and challenge-response authentication using the Server Identity Key.
 */
class PhobriWebSocketClient(
    private val pairingManager: PairingManager,
    private val client: HttpClient = defaultClient(pairingManager)
) {
    companion object {
        private const val TAG = "PhobriWS"
        private val json = Json {
            ignoreUnknownKeys = true
            isLenient = true // Accept case-insensitive enums from server
            coerceInputValues = true
        }

        fun defaultClient(pairingManager: PairingManager? = null): HttpClient {
            val pinnedTrustManager = pairingManager?.createPinnedTrustManager()

            return HttpClient(OkHttp) {
                install(WebSockets) {
                    pingIntervalMillis = 30_000
                }

                engine {
                    config {
                        // Skip hostname verification — the cert CN is "localhost"
                        // but we connect via Tailscale IP/hostname.
                        // Certificate authenticity is verified by the TrustManager below.
                        hostnameVerifier(HostnameVerifier { _, _ -> true })

                        val trustManager = pinnedTrustManager ?: trustAllManager
                        val sslContext = javax.net.ssl.SSLContext.getInstance("TLS")
                        sslContext.init(null, arrayOf(trustManager), java.security.SecureRandom())
                        sslSocketFactory(sslContext.socketFactory, trustManager as X509TrustManager)
                    }
                }
            }
        }

        /** TrustManager that accepts any certificate. Used only when no pairing
         *  fingerprint is stored (first-time pairing flow). */
        private val trustAllManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
    }

    private var session: WebSocketSession? = null
    private var listenJob: Job? = null

    private val _connectionState = MutableStateFlow(false)
    val connectionState: StateFlow<Boolean> = _connectionState.asStateFlow()

    private val _incomingMessages = MutableSharedFlow<ProtocolMessage>(replay = 0)
    val incomingMessages: SharedFlow<ProtocolMessage> = _incomingMessages.asSharedFlow()

    /** Count of messages sent (incremented on each successful send). */
    private val _sentCount = MutableStateFlow(0)
    val sentCount: StateFlow<Int> = _sentCount.asStateFlow()

    /** Count of messages received. */
    private val _receivedCount = MutableStateFlow(0)
    val receivedCount: StateFlow<Int> = _receivedCount.asStateFlow()

    /** Last error message (for UI display). */
    private val _lastError = MutableStateFlow<String?>(null)
    val lastError: StateFlow<String?> = _lastError.asStateFlow()

    /** Event log for debugging (last 50 events). */
    private val _eventLog = MutableStateFlow<List<ClientEvent>>(emptyList())
    val eventLog: StateFlow<List<ClientEvent>> = _eventLog.asStateFlow()

    private fun addEvent(type: String, detail: String) {
        val event = ClientEvent(type = type, detail = detail)
        _eventLog.value = (_eventLog.value + event).takeLast(50)
        Log.d(TAG, "[$type] $detail")
    }

    /**
     * Connect to the desktop server.
     * Cleans up any previous connection first, then establishes a new one.
     * Returns immediately after pair.init is sent; listening runs in background.
     */
    suspend fun connect(url: String, token: String) {
        // Clean up any previous connection first
        disconnect()

        addEvent("connect", "Connecting to $url ...")
        _lastError.value = null
        try {
            val wsSession = client.webSocketSession(url)
            session = wsSession
            _connectionState.value = true
            addEvent("connect", "TLS handshake OK, sending pair.init")

            val initMsg = """{"type":"request","action":"pair.init","payload":{"token":"$token"}}"""
            wsSession.send(Frame.Text(initMsg))
            _sentCount.value++
            addEvent("send", "pair.init (token=${token.take(8)}...)")

            // Start listening in background
            listenJob = CoroutineScope(Dispatchers.IO).launch {
                try {
                    addEvent("connect", "Listening for messages...")
                    for (frame in wsSession.incoming) {
                        if (frame is Frame.Text) {
                            val text = frame.readText()
                            _receivedCount.value++
                            try {
                                val msg = json.decodeFromString<ProtocolMessage>(text)
                                addEvent("recv", "${msg.type}/${msg.action}${msg.id?.let { " id=$it" } ?: ""}")
                                _incomingMessages.emit(msg)
                            } catch (e: Exception) {
                                addEvent("recv", "Raw: ${text.take(250)}")
                            }
                        }
                    }
                    addEvent("disconnect", "Connection closed by server")
                } catch (e: Exception) {
                    if (e !is kotlinx.coroutines.CancellationException) {
                        addEvent("disconnect", "Connection lost: ${e.message}")
                    }
                } finally {
                    session = null
                    _connectionState.value = false
                }
            }
        } catch (e: Exception) {
            val errorMsg = "${e.javaClass.simpleName}: ${e.message ?: "unknown"}"
            _lastError.value = errorMsg
            addEvent("error", errorMsg)
            Log.e(TAG, "Connection failed", e)
            session = null
            _connectionState.value = false
        }
    }

    /**
     * Perform challenge-response authentication with the server.
     * Awaits the auth.challenge response deterministically via flow filtering — no
     * busy-wait polling or race-prone job management.
     */
    suspend fun performChallengeResponse(): Boolean {
        val sikBase64 = pairingManager.serverIdentityKey
        if (sikBase64 == null) {
            return true // First-time pairing, skip challenge
        }

        val sik = try {
            Base64.decode(sikBase64, Base64.DEFAULT)
        } catch (e: Exception) {
            addEvent("error", "Failed to decode SIK: ${e.message}")
            return false
        }

        val nonce = generateNonce()
        val ts = System.currentTimeMillis()

        val challengePayload = json.encodeToJsonElement(
            AuthChallengePayload.serializer(),
            AuthChallengePayload(nonce = nonce, ts = ts)
        )
        val challengeMsg = ProtocolMessage(
            type = MessageType.REQUEST,
            action = "auth.challenge",
            id = "chal-" + nonce.take(8),
            payload = challengePayload
        )
        sendMessage(challengeMsg)
        addEvent("send", "auth.challenge")

        val message = "$nonce|$ts"
        val expectedHmac = computeHmacSha256Hex(sik, message)

        val response = withTimeoutOrNull(10_000) {
            incomingMessages
                .filter { it.action == "auth.challenge" && it.type == MessageType.RESPONSE }
                .first()
        }

        val success = if (response != null) {
            val serverHmac = response.payload?.jsonObject?.get("hmac")?.jsonPrimitive?.content
            serverHmac != null && serverHmac == expectedHmac
        } else {
            false
        }

        addEvent(if (success) "auth" else "error",
            if (success) "Challenge-response OK" else "Challenge-response FAILED")
        return success
    }

    suspend fun sendMessage(message: ProtocolMessage) {
        session?.let { ws ->
            val text = json.encodeToString(ProtocolMessage.serializer(), message)
            ws.send(Frame.Text(text))
            _sentCount.value++
            addEvent("send", "${message.type}/${message.action}")
        }
    }

    suspend fun pushNewSms(sms: SmsMessage) {
        val payload = json.encodeToJsonElement(SmsMessage.serializer(), sms)
        val msg = ProtocolMessage(type = MessageType.PUSH, action = "sms.new", payload = payload)
        sendMessage(msg)
    }

    suspend fun pushNewCall(call: CallLogEntry) {
        val payload = json.encodeToJsonElement(CallLogEntry.serializer(), call)
        val msg = ProtocolMessage(type = MessageType.PUSH, action = "call.new", payload = payload)
        sendMessage(msg)
    }

    suspend fun sendSmsSync(messages: List<SmsMessage>, hasMore: Boolean = false) {
        val syncPayload = SmsSyncPayload(messages = messages, hasMore = hasMore)
        val payload = json.encodeToJsonElement(SmsSyncPayload.serializer(), syncPayload)
        val msg = ProtocolMessage(type = MessageType.PUSH, action = "sms.sync", payload = payload)
        sendMessage(msg)
        addEvent("send", "sms.sync (${messages.size} messages)")
    }

    suspend fun sendCallSync(calls: List<CallLogEntry>, hasMore: Boolean = false) {
        val syncPayload = CallSyncPayload(calls = calls, hasMore = hasMore)
        val payload = json.encodeToJsonElement(CallSyncPayload.serializer(), syncPayload)
        val msg = ProtocolMessage(type = MessageType.PUSH, action = "call.sync", payload = payload)
        sendMessage(msg)
        addEvent("send", "call.sync (${calls.size} calls)")
    }

    suspend fun disconnect() {
        val job = listenJob
        listenJob = null
        if (job != null) {
            job.cancel()
            try { withTimeout(3000) { job.join() } } catch (_: Exception) { }
        }
        try {
            session?.close()
        } catch (_: Exception) { }
        session = null
        if (_connectionState.value) {
            _connectionState.value = false
        }
    }

    private fun generateNonce(): String {
        val bytes = ByteArray(32)
        java.security.SecureRandom().nextBytes(bytes)
        return bytes.joinToString("") { "%02x".format(it) }
    }

    private fun computeHmacSha256Hex(key: ByteArray, message: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        val hmacBytes = mac.doFinal(message.toByteArray(Charsets.UTF_8))
        return hmacBytes.joinToString("") { "%02x".format(it) }
    }
}
