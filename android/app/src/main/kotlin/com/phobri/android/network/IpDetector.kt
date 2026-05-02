package com.phobri.android.network

import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * Utility for detecting the device's local IP address.
 */
object IpDetector {

    /**
     * Get the device's local (LAN) IPv4 address.
     * @return IPv4 address string, or null if not connected.
     */
    fun getLocalIpAddress(): String? {
        return try {
            NetworkInterface.getNetworkInterfaces()?.asSequence()
                ?.flatMap { it.inetAddresses.asSequence() }
                ?.firstOrNull { addr ->
                    !addr.isLoopbackAddress && addr is Inet4Address
                }
                ?.hostAddress
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Check if a given IP is on the local network.
     * Simple heuristic: assumes local network uses 192.168.x.x, 10.x.x.x, or 172.16-31.x.x.
     */
    fun isLocalNetwork(ip: String): Boolean {
        return ip.startsWith("192.168.") ||
               ip.startsWith("10.") ||
               ip.matches(Regex("^172\\.(1[6-9]|2[0-9]|3[0-1])\\..*"))
    }
}
