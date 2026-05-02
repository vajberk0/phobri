package com.phobri.android.pairing

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class PairingManagerTests {

    private lateinit var context: Context
    private lateinit var pairingManager: PairingManager

    @Before
    fun setup() {
        context = ApplicationProvider.getApplicationContext()
        pairingManager = PairingManager(context)
        pairingManager.clearPairing()
    }

    @After
    fun cleanup() {
        pairingManager.clearPairing()
    }

    @Test
    fun `new manager is not paired`() {
        assertFalse(pairingManager.isPaired)
        assertNull(pairingManager.pairingToken)
    }

    @Test
    fun `save pairing sets paired state`() {
        pairingManager.savePairing(
            token = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6abcd",
            fingerprint = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            host = "192.168.1.10",
            port = 8765
        )

        assertTrue(pairingManager.isPaired)
        assertEquals("192.168.1.10", pairingManager.desktopHost)
        assertEquals(8765, pairingManager.desktopPort)
    }

    @Test
    fun `clear pairing removes state`() {
        pairingManager.savePairing("token", "fingerprint", "host")
        assertTrue(pairingManager.isPaired)

        pairingManager.clearPairing()
        assertFalse(pairingManager.isPaired)
        assertNull(pairingManager.pairingToken)
    }

    @Test
    fun `save pairing stores SIK when provided`() {
        val sik = "aGVsbG8gd29ybGQgdGhpcyBpcyBhIHRlc3Qgc2VjcmV0"
        pairingManager.savePairing(
            token = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6abcd",
            fingerprint = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            host = "192.168.1.10",
            port = 8765,
            sik = sik
        )

        assertEquals(sik, pairingManager.serverIdentityKey)
    }

    @Test
    fun `save pairing without SIK leaves SIK null`() {
        pairingManager.savePairing(
            token = "token",
            fingerprint = "fingerprint",
            host = "host",
            port = 8765
            // No SIK provided
        )

        assertNull(pairingManager.serverIdentityKey)
    }

    @Test
    fun `clear pairing removes SIK`() {
        pairingManager.savePairing(
            token = "token",
            fingerprint = "fingerprint",
            host = "host",
            sik = "somesik"
        )
        assertNotNull(pairingManager.serverIdentityKey)

        pairingManager.clearPairing()
        assertNull(pairingManager.serverIdentityKey)
    }

    @Test
    fun `update host changes stored host`() {
        pairingManager.savePairing("token", "fingerprint", "192.168.1.10")
        pairingManager.updateHost("10.0.0.5")

        assertEquals("10.0.0.5", pairingManager.desktopHost)
    }
}
