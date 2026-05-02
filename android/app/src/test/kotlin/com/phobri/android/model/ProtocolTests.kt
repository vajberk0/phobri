package com.phobri.android.model

import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class ProtocolTests {

    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun `sms message serializes and deserializes`() {
        val original = SmsMessage(
            id = 12345,
            threadId = 678,
            address = "+15551234567",
            contactName = "Mom",
            body = "Are you coming for dinner?",
            date = 1714608000000,
            type = SmsType.INBOX,
            read = true
        )

        val jsonStr = json.encodeToString(SmsMessage.serializer(), original)
        val deserialized = json.decodeFromString<SmsMessage>(jsonStr)

        assertEquals(original.id, deserialized.id)
        assertEquals(original.address, deserialized.address)
        assertEquals(original.body, deserialized.body)
        assertEquals(original.type, deserialized.type)
    }

    @Test
    fun `call log entry serializes and deserializes`() {
        val original = CallLogEntry(
            id = 789,
            number = "+15551234567",
            contactName = "Mom",
            date = 1714608000000,
            duration = 120,
            type = CallType.INCOMING
        )

        val jsonStr = json.encodeToString(CallLogEntry.serializer(), original)
        val deserialized = json.decodeFromString<CallLogEntry>(jsonStr)

        assertEquals(original.id, deserialized.id)
        assertEquals(original.number, deserialized.number)
        assertEquals(original.duration, deserialized.duration)
        assertEquals(original.type, deserialized.type)
    }

    @Test
    fun `sms displayName falls back to address`() {
        val withContact = SmsMessage(address = "+123", contactName = "Bob")
        assertEquals("Bob", withContact.displayName)

        val withoutContact = SmsMessage(address = "+123", contactName = null)
        assertEquals("+123", withoutContact.displayName)
    }

    @Test
    fun `call duration formatted correctly`() {
        val call = CallLogEntry(duration = 125)
        assertEquals("2:05", call.durationFormatted)

        val zeroCall = CallLogEntry(duration = 0)
        assertEquals("0:00", zeroCall.durationFormatted)
    }

    @Test
    fun `protocol message request serializes`() {
        val msg = ProtocolMessage(
            type = MessageType.REQUEST,
            action = "sms.sync.request",
            id = "abc123"
        )

        val jsonStr = json.encodeToString(ProtocolMessage.serializer(), msg)
        val deserialized = json.decodeFromString<ProtocolMessage>(jsonStr)

        assertEquals(MessageType.REQUEST, deserialized.type)
        assertEquals("sms.sync.request", deserialized.action)
        assertEquals("abc123", deserialized.id)
    }
}
