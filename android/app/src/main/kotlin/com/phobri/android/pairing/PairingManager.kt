package com.phobri.android.pairing

import android.content.Context
import android.content.SharedPreferences
import android.util.Base64
import java.security.MessageDigest
import java.security.cert.X509Certificate
import javax.net.ssl.*

/**
 * Manages the Trust-on-First-Use pairing and certificate pinning.
 * Stores pairing token and pinned certificate fingerprint in SharedPreferences.
 */
class PairingManager(context: Context) {

    private val prefs: SharedPreferences = context.getSharedPreferences("phobri_pairing", Context.MODE_PRIVATE)

    companion object {
        private const val KEY_PAIRING_TOKEN = "pairing_token"
        private const val KEY_CERT_FINGERPRINT = "cert_fingerprint"
        private const val KEY_DESKTOP_HOST = "desktop_host"
        private const val KEY_DESKTOP_PORT = "desktop_port"
        private const val KEY_SERVER_IDENTITY_KEY = "server_identity_key"
    }

    /** Whether a pairing exists. */
    val isPaired: Boolean get() = prefs.getString(KEY_PAIRING_TOKEN, null) != null

    /** The stored pairing token. */
    val pairingToken: String? get() = prefs.getString(KEY_PAIRING_TOKEN, null)

    /** The pinned certificate fingerprint. */
    val certFingerprint: String? get() = prefs.getString(KEY_CERT_FINGERPRINT, null)

    /** The server identity key (SIK) for challenge-response verification. */
    val serverIdentityKey: String? get() = prefs.getString(KEY_SERVER_IDENTITY_KEY, null)

    /** Last known desktop host. */
    val desktopHost: String? get() = prefs.getString(KEY_DESKTOP_HOST, null)

    /** Last known desktop port. */
    val desktopPort: Int get() = prefs.getInt(KEY_DESKTOP_PORT, 8765)

    /**
     * Store pairing information.
     */
    fun savePairing(token: String, fingerprint: String, host: String, port: Int = 8765, sik: String? = null) {
        val editor = prefs.edit()
            .putString(KEY_PAIRING_TOKEN, token)
            .putString(KEY_CERT_FINGERPRINT, fingerprint)
            .putString(KEY_DESKTOP_HOST, host)
            .putInt(KEY_DESKTOP_PORT, port)

        if (sik != null) {
            editor.putString(KEY_SERVER_IDENTITY_KEY, sik)
        }

        editor.apply()
    }

    /**
     * Clear pairing information.
     */
    fun clearPairing() {
        prefs.edit()
            .remove(KEY_PAIRING_TOKEN)
            .remove(KEY_CERT_FINGERPRINT)
            .remove(KEY_DESKTOP_HOST)
            .remove(KEY_DESKTOP_PORT)
            .remove(KEY_SERVER_IDENTITY_KEY)
            .apply()
    }

    /**
     * Update the desktop host (e.g., when IP changes).
     */
    fun updateHost(host: String) {
        prefs.edit().putString(KEY_DESKTOP_HOST, host).apply()
    }

    /**
     * Compute SHA-256 fingerprint of a certificate.
     */
    fun computeFingerprint(cert: X509Certificate): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val hash = digest.digest(cert.encoded)
        return hash.joinToString("") { "%02x".format(it) }
    }

    /**
     * Create an SSL socket factory that pins the certificate fingerprint.
     * Verifies the server certificate matches our stored fingerprint.
     */
    fun createPinnedSslSocketFactory(): SSLSocketFactory? {
        val pinnedFingerprint = certFingerprint ?: return null
        val tm = createPinnedTrustManager() ?: return null
        return try {
            val sslContext = SSLContext.getInstance("TLSv1.3")
            sslContext.init(null, arrayOf(tm), java.security.SecureRandom())
            sslContext.socketFactory
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Create an X509TrustManager that validates the server certificate
     * against the pinned SHA-256 fingerprint. Returns null if no valid
     * fingerprint is stored (i.e., first-time pairing in progress).
     */
    fun createPinnedTrustManager(): X509TrustManager? {
        val pinnedFingerprint = certFingerprint
        if (pinnedFingerprint.isNullOrBlank() || pinnedFingerprint.length < 64) return null

        return object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                chain?.firstOrNull()?.let { cert ->
                    val fingerprint = computeFingerprint(cert)
                    if (fingerprint != pinnedFingerprint) {
                        throw SSLException(
                            "Certificate fingerprint mismatch!\n" +
                            "Expected: $pinnedFingerprint\n" +
                            "Got: $fingerprint"
                        )
                    }
                } ?: throw SSLException("No server certificate provided")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
    }
}
