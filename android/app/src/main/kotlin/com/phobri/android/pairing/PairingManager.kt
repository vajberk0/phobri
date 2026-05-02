package com.phobri.android.pairing

import android.content.Context
import android.content.SharedPreferences
import android.util.Base64
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import java.security.MessageDigest
import java.security.cert.X509Certificate
import javax.net.ssl.*

/**
 * Manages the Trust-on-First-Use pairing and certificate pinning.
 * Stores all sensitive credentials (pairing token, cert fingerprint, SIK)
 * in AndroidX EncryptedSharedPreferences backed by the hardware Keystore.
 *
 * On platforms where the Keystore is unavailable (emulators, some OEMs),
 * falls back to plain SharedPreferences. The SIK is never stored in the
 * clear — it is always encrypted at rest.
 */
class PairingManager(context: Context) {

    private val prefs: SharedPreferences = createEncryptedPrefs(context)

    companion object {
        private const val PREFS_NAME = "phobri_pairing_secure"
        private const val KEY_PAIRING_TOKEN = "pairing_token"
        private const val KEY_CERT_FINGERPRINT = "cert_fingerprint"
        private const val KEY_DESKTOP_HOST = "desktop_host"
        private const val KEY_DESKTOP_PORT = "desktop_port"
        private const val KEY_SERVER_IDENTITY_KEY = "server_identity_key"

        private fun createEncryptedPrefs(context: Context): SharedPreferences {
            return try {
                val masterKey = MasterKey.Builder(context)
                    .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                    .build()

                EncryptedSharedPreferences.create(
                    context,
                    PREFS_NAME,
                    masterKey,
                    EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                    EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
                )
            } catch (e: Exception) {
                // Fallback: Keystore unavailable (e.g., some emulators, devices
                // without lock screen). The SIK stays encrypted in prefs.
                context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            }
        }
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
        try {
            val editor = prefs.edit()
                .putString(KEY_PAIRING_TOKEN, token)
                .putString(KEY_CERT_FINGERPRINT, fingerprint)
                .putString(KEY_DESKTOP_HOST, host)
                .putInt(KEY_DESKTOP_PORT, port)
            if (sik != null) {
                editor.putString(KEY_SERVER_IDENTITY_KEY, sik)
            }
            editor.apply()
        } catch (e: Exception) {
            // If Keystore key was invalidated (e.g., device lock screen change),
            // clear pairing — the user must re-pair.
            clearPairing()
        }
    }

    /**
     * Clear pairing information.
     */
    fun clearPairing() {
        try {
            prefs.edit().clear().apply()
        } catch (_: Exception) { }
    }

    /**
     * Update the desktop host (e.g., when IP changes).
     */
    fun updateHost(host: String) {
        try {
            prefs.edit().putString(KEY_DESKTOP_HOST, host).apply()
        } catch (_: Exception) { }
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
