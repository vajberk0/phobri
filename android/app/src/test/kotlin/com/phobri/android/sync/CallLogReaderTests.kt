package com.phobri.android.sync

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class CallLogReaderTests {

    private val context: Context = ApplicationProvider.getApplicationContext()

    @Test
    fun `reader can be constructed`() {
        val reader = CallLogReader(context)
        assertNotNull(reader)
    }

    @Test
    fun `read call log returns empty list by default`() {
        val reader = CallLogReader(context)
        val calls = reader.readCallLog()
        assertTrue(calls.isEmpty())
    }

    @Test
    fun `read recent calls returns empty list by default`() {
        val reader = CallLogReader(context)
        val calls = reader.readRecentCalls()
        assertTrue(calls.isEmpty())
    }
}
