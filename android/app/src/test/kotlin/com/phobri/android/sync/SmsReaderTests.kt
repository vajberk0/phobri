package com.phobri.android.sync

import android.content.Context
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class SmsReaderTests {

    private val context: Context = ApplicationProvider.getApplicationContext()

    @Test
    fun `reader can be constructed`() {
        val reader = SmsReader(context)
        assertNotNull(reader)
    }

    @Test
    fun `read messages returns empty list by default`() {
        val reader = SmsReader(context)
        val messages = reader.readMessages()
        // No test SMS data inserted, should return empty
        assertTrue(messages.isEmpty())
    }

    @Test
    fun `read recent messages returns empty list by default`() {
        val reader = SmsReader(context)
        val messages = reader.readRecentMessages()
        assertTrue(messages.isEmpty())
    }

    @Test
    fun `read conversation returns empty for unknown address`() {
        val reader = SmsReader(context)
        val messages = reader.readConversation("+1234567890")
        assertTrue(messages.isEmpty())
    }
}
