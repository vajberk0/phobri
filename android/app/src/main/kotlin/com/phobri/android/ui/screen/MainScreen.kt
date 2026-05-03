package com.phobri.android.ui.screen

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.phobri.android.BuildConfig
import com.phobri.android.pairing.PairingManager
import com.phobri.android.service.SyncForegroundService
import com.phobri.android.sync.ClientEvent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.URL
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import androidx.compose.runtime.rememberCoroutineScope

/**
 * Main screen for the Android app.
 * Shows pairing, real-time connection status, message counters, and event log.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    onStartSync: (host: String, port: Int) -> Unit,
    onStopSync: () -> Unit,
    onPermissionsRequested: () -> Unit
) {
    val context = LocalContext.current
    val pairingManager = remember { PairingManager(context) }

    var isPaired by remember { mutableStateOf(pairingManager.isPaired) }
    var pairingCode by remember { mutableStateOf("") }
    var pairingError by remember { mutableStateOf<String?>(null) }
    var isPairing by remember { mutableStateOf(false) }
    var manualHost by remember { mutableStateOf(pairingManager.desktopHost ?: "") }
    var manualPort by remember { mutableStateOf(pairingManager.desktopPort.toString()) }
    val coroutineScope = rememberCoroutineScope()

    // Reactive permission check
    var permissionCheckTick by remember { mutableStateOf(0) }
    val permissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions()
    ) { permissionCheckTick++ }

    val hasPermissions = remember(permissionCheckTick) {
        requiredPermissions.all { perm ->
            ContextCompat.checkSelfPermission(context, perm) == PackageManager.PERMISSION_GRANTED
        }
    }

    // Collect real connection state from the foreground service
    val isSyncing by SyncForegroundService.isServiceRunning.collectAsState()
    val connected by SyncForegroundService.connectionState.collectAsState()
    val sentCount by SyncForegroundService.sentCount.collectAsState()
    val receivedCount by SyncForegroundService.receivedCount.collectAsState()
    val lastError by SyncForegroundService.lastError.collectAsState()
    val eventLog by SyncForegroundService.eventLog.collectAsState()

    val listState = rememberLazyListState()
    val clipboardManager = LocalClipboardManager.current
    var showLogDialog by remember { mutableStateOf(false) }

    // Auto-scroll event log to bottom
    LaunchedEffect(eventLog.size) {
        if (eventLog.isNotEmpty()) {
            listState.animateScrollToItem(eventLog.size - 1)
        }
    }

    // Log dialog
    if (showLogDialog) {
        val logItems = remember(eventLog) { eventLog.toList() }
        AlertDialog(
            onDismissRequest = { showLogDialog = false },
            title = { 
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Event Log", modifier = Modifier.weight(1f))
                    TextButton(onClick = {
                        val text = logItems.joinToString("\n") { "${it.type}: ${it.detail}" }
                        clipboardManager.setText(AnnotatedString(text))
                    }) {
                        Text("Copy", style = MaterialTheme.typography.labelSmall)
                    }
                }
            },
            text = {
                if (logItems.isEmpty()) {
                    Text("No events yet.")
                } else {
                    LazyColumn(
                        modifier = Modifier.fillMaxWidth().heightIn(max = 400.dp)
                    ) {
                        items(logItems.size) { index ->
                            val event = logItems[index]
                            LogEntry(event)
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { showLogDialog = false }) {
                    Text("Close")
                }
            }
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("📱 Phobri") },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primaryContainer
                )
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            // ── Status Card ──
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(
                    containerColor = when {
                        connected -> MaterialTheme.colorScheme.primaryContainer
                        isSyncing -> MaterialTheme.colorScheme.tertiaryContainer
                        else -> MaterialTheme.colorScheme.surfaceVariant
                    }
                )
            ) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            imageVector = when {
                                connected -> Icons.Default.CheckCircle
                                isSyncing -> Icons.Default.Sync
                                else -> Icons.Default.Warning
                            },
                            contentDescription = null,
                            tint = when {
                                connected -> MaterialTheme.colorScheme.primary
                                isSyncing -> MaterialTheme.colorScheme.tertiary
                                else -> MaterialTheme.colorScheme.error
                            }
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            text = when {
                                connected -> "Connected"
                                isSyncing -> "Connecting..."
                                else -> "Not syncing"
                            },
                            style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.weight(1f)
                        )
                        // Log button
                        if (eventLog.isNotEmpty() || isSyncing) {
                            FilledTonalButton(
                                onClick = { showLogDialog = true },
                                contentPadding = PaddingValues(horizontal = 8.dp, vertical = 4.dp)
                            ) {
                                Icon(
                                    Icons.Default.Menu,
                                    contentDescription = "Log",
                                    modifier = Modifier.size(16.dp)
                                )
                                Spacer(modifier = Modifier.width(4.dp))
                                Text("Log", style = MaterialTheme.typography.labelSmall)
                            }
                        }
                    }

                    // Counters
                    if (isSyncing) {
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(
                            text = "Sent: $sentCount  •  Received: $receivedCount",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }

                    // Last error (selectable)
                    if (lastError != null) {
                        Spacer(modifier = Modifier.height(4.dp))
                        SelectionContainer {
                            Text(
                                text = "⚠ ${lastError!!}",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.error
                            )
                        }
                    }
                }
            }

            // ── Permissions (if needed) ──
            if (!hasPermissions) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    )
                ) {
                    Column(modifier = Modifier.padding(12.dp)) {
                        Text("Permissions Required", style = MaterialTheme.typography.titleMedium)
                        Spacer(modifier = Modifier.height(4.dp))
                        Button(onClick = {
                            permissionLauncher.launch(requiredPermissions.toTypedArray())
                        }) {
                            Text("Grant Permissions")
                        }
                    }
                }
            }

            // ── Pairing ──
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Text("Device Pairing", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(6.dp))

                    if (!isPaired) {
                        OutlinedTextField(
                            value = pairingCode,
                            onValueChange = { pairingCode = it },
                            label = { Text("Pairing Token") },
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true,
                            visualTransformation = PasswordVisualTransformation()
                        )
                        Spacer(modifier = Modifier.height(6.dp))
                        // Fetch fingerprint from server before saving pairing
                        val trustAllSslContext by remember {
                            mutableStateOf(
                                try {
                                    SSLContext.getInstance("TLS").apply {
                                        init(null, arrayOf<TrustManager>(object : X509TrustManager {
                                            override fun checkClientTrusted(c: Array<out X509Certificate>?, a: String?) {}
                                            override fun checkServerTrusted(c: Array<out X509Certificate>?, a: String?) {}
                                            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
                                        }), java.security.SecureRandom())
                                    }
                                } catch (e: Exception) { null }
                            )
                        }

                        pairingError?.let { error ->
                            Text(error, color = MaterialTheme.colorScheme.error,
                                style = MaterialTheme.typography.bodySmall)
                            Spacer(modifier = Modifier.height(4.dp))
                        }

                        Button(
                            onClick = {
                                if (pairingCode.isNotBlank() && !isPairing) {
                                    val host = manualHost.ifBlank { "192.168.1.1" }
                                    val port = manualPort.toIntOrNull() ?: 8765
                                    val token = pairingCode
                                    isPairing = true
                                    pairingError = null

                                    coroutineScope.launch {
                                        val fingerprint = withContext(Dispatchers.IO) {
                                            try {
                                                val url = URL("https://$host:$port/api/v1/ping")
                                                val conn = url.openConnection() as HttpsURLConnection
                                                conn.sslSocketFactory = trustAllSslContext?.socketFactory
                                                conn.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
                                                conn.connectTimeout = 5000
                                                conn.readTimeout = 5000
                                                conn.requestMethod = "GET"

                                                if (conn.responseCode == 200) {
                                                    val body = conn.inputStream.bufferedReader().readText()
                                                    conn.disconnect()
                                                    JSONObject(body).optString("fingerprint", "")
                                                } else {
                                                    conn.disconnect()
                                                    ""
                                                }
                                            } catch (e: Exception) {
                                                ""
                                            }
                                        }

                                        if (fingerprint.isNotBlank() && fingerprint.length == 64) {
                                            pairingManager.savePairing(
                                                token = token,
                                                fingerprint = fingerprint,
                                                host = host,
                                                port = port
                                            )
                                            isPaired = true
                                            pairingError = null
                                        } else {
                                            pairingError = "Could not reach server at $host:$port"
                                        }
                                        isPairing = false
                                    }
                                }
                            },
                            modifier = Modifier.fillMaxWidth(),
                            enabled = !isPairing
                        ) {
                            if (isPairing) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(16.dp),
                                    strokeWidth = 2.dp
                                )
                                Spacer(modifier = Modifier.width(8.dp))
                            }
                            Text(if (isPairing) "Connecting..." else "Pair Device")
                        }
                    } else {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Default.CheckCircle, contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary)
                            Spacer(modifier = Modifier.width(8.dp))
                            Text("Paired to desktop", style = MaterialTheme.typography.bodyLarge)
                        }
                        Spacer(modifier = Modifier.height(4.dp))
                        TextButton(onClick = {
                            pairingManager.clearPairing()
                            isPaired = false
                        }) {
                            Text("Unpair", color = MaterialTheme.colorScheme.error)
                        }
                    }
                }
            }

            // ── Connection Settings ──
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Text("Connection", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(6.dp))
                    OutlinedTextField(
                        value = manualHost,
                        onValueChange = { manualHost = it },
                        label = { Text("Desktop IP / Hostname") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true
                    )
                    Spacer(modifier = Modifier.height(6.dp))
                    OutlinedTextField(
                        value = manualPort,
                        onValueChange = { manualPort = it },
                        label = { Text("Port") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true
                    )
                }
            }

            // ── Sync Settings ──
            var syncSms by remember { mutableStateOf(pairingManager.syncSmsEnabled) }
            var syncCalls by remember { mutableStateOf(pairingManager.syncCallsEnabled) }
            var maxEntries by remember { mutableStateOf(pairingManager.maxSyncEntries.toString()) }

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Text("Sync Settings", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(6.dp))

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Checkbox(
                            checked = syncSms,
                            onCheckedChange = {
                                syncSms = it
                                pairingManager.syncSmsEnabled = it
                            }
                        )
                        Text("Sync SMS messages", style = MaterialTheme.typography.bodyLarge)
                    }

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Checkbox(
                            checked = syncCalls,
                            onCheckedChange = {
                                syncCalls = it
                                pairingManager.syncCallsEnabled = it
                            }
                        )
                        Text("Sync call logs", style = MaterialTheme.typography.bodyLarge)
                    }

                    Spacer(modifier = Modifier.height(6.dp))
                    OutlinedTextField(
                        value = maxEntries,
                        onValueChange = { newVal ->
                            maxEntries = newVal
                            val parsed = newVal.toIntOrNull()
                            if (parsed != null && parsed > 0) {
                                pairingManager.maxSyncEntries = parsed
                            }
                        },
                        label = { Text("Max entries to send") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        supportingText = {
                            Text("Limits how many SMS/calls are sent per initial sync")
                        }
                    )
                }
            }

            // Spacer to push button to bottom
            Spacer(modifier = Modifier.weight(1f))

            // Version
            Text(
                text = "v${BuildConfig.VERSION_NAME}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                modifier = Modifier.align(Alignment.CenterHorizontally).padding(bottom = 4.dp)
            )

            // ── Sync Control Button ──
            Button(
                onClick = {
                    if (isSyncing) {
                        onStopSync()
                    } else {
                        onStartSync(
                            manualHost.ifBlank { "192.168.1.1" },
                            manualPort.toIntOrNull() ?: 8765
                        )
                    }
                },
                modifier = Modifier.fillMaxWidth().height(48.dp),
                enabled = hasPermissions && isPaired,
                colors = ButtonDefaults.buttonColors(
                    containerColor = if (isSyncing)
                        MaterialTheme.colorScheme.error
                    else
                        MaterialTheme.colorScheme.primary
                )
            ) {
                Icon(
                    imageVector = if (isSyncing) Icons.Default.Stop else Icons.Default.PlayArrow,
                    contentDescription = null
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(if (isSyncing) "Stop Sync" else "Start Sync")
            }
        }
    }
}

@Composable
private fun LogEntry(event: ClientEvent) {
    val emoji = when (event.type) {
        "connect" -> "🔌"
        "disconnect" -> "🔌"
        "send" -> "⬆"
        "recv" -> "⬇"
        "error" -> "❌"
        "auth" -> "🔐"
        else -> "•"
    }
    val color = when (event.type) {
        "error" -> MaterialTheme.colorScheme.error
        "send" -> MaterialTheme.colorScheme.primary
        "recv" -> MaterialTheme.colorScheme.tertiary
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    SelectionContainer {
        Text(
            text = "$emoji ${event.detail}",
            style = MaterialTheme.typography.bodySmall.copy(
                fontSize = 11.sp,
                fontFamily = FontFamily.Monospace
            ),
            color = color,
            modifier = Modifier.padding(vertical = 1.dp)
        )
    }
}

private val requiredPermissions = mutableListOf(
    Manifest.permission.READ_SMS,
    Manifest.permission.RECEIVE_SMS,
    Manifest.permission.READ_CALL_LOG,
    Manifest.permission.READ_PHONE_STATE,
    Manifest.permission.READ_CONTACTS
).apply {
    if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.TIRAMISU) {
        add(Manifest.permission.POST_NOTIFICATIONS)
    }
}
