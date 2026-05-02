package com.phobri.android.ui.screen

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.phobri.android.pairing.PairingManager
import com.phobri.android.network.IpDetector

/**
 * Main screen for the Android app.
 * Shows pairing, connection status, and server controls.
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
    var isSyncing by remember { mutableStateOf(false) }
    var pairingCode by remember { mutableStateOf("") }
    var manualHost by remember { mutableStateOf(pairingManager.desktopHost ?: "") }
    var manualPort by remember { mutableStateOf(pairingManager.desktopPort.toString()) }
    var localIp by remember { mutableStateOf(IpDetector.getLocalIpAddress() ?: "Unknown") }

    val hasPermissions = requiredPermissions.all { perm ->
        ContextCompat.checkSelfPermission(context, perm) == PackageManager.PERMISSION_GRANTED
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
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            // Status Card
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(
                    containerColor = if (isSyncing)
                        MaterialTheme.colorScheme.primaryContainer
                    else
                        MaterialTheme.colorScheme.surfaceVariant
                )
            ) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("Status", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(8.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            imageVector = if (isSyncing) Icons.Default.CheckCircle else Icons.Default.Warning,
                            contentDescription = null,
                            tint = if (isSyncing)
                                MaterialTheme.colorScheme.primary
                            else
                                MaterialTheme.colorScheme.error
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            text = if (isSyncing) "Syncing" else "Not syncing",
                            style = MaterialTheme.typography.bodyLarge
                        )
                    }
                    Text(
                        text = "Local IP: $localIp",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            // Permissions Card
            if (!hasPermissions) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    )
                ) {
                    Column(modifier = Modifier.padding(16.dp)) {
                        Text("Permissions Required",
                            style = MaterialTheme.typography.titleMedium)
                        Spacer(modifier = Modifier.height(4.dp))
                        Text("Phobri needs SMS, Call Log, and Phone permissions to sync data.",
                            style = MaterialTheme.typography.bodySmall)
                        Spacer(modifier = Modifier.height(8.dp))
                        Button(onClick = onPermissionsRequested) {
                            Text("Grant Permissions")
                        }
                    }
                }
            }

            // Pairing Section
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("Device Pairing", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(8.dp))

                    if (!isPaired) {
                        Text(
                            "Enter the pairing token shown on your desktop:",
                            style = MaterialTheme.typography.bodySmall
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        OutlinedTextField(
                            value = pairingCode,
                            onValueChange = { pairingCode = it },
                            label = { Text("Pairing Token") },
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true,
                            visualTransformation = PasswordVisualTransformation()
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Button(
                            onClick = {
                                if (pairingCode.isNotBlank()) {
                                    pairingManager.savePairing(
                                        token = pairingCode,
                                        fingerprint = "",
                                        host = manualHost.ifBlank { "192.168.1.1" },
                                        port = manualPort.toIntOrNull() ?: 8765
                                    )
                                    isPaired = true
                                }
                            },
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text("Pair Device")
                        }
                    } else {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Default.CheckCircle, contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary)
                            Spacer(modifier = Modifier.width(8.dp))
                            Text("Paired to desktop", style = MaterialTheme.typography.bodyLarge)
                        }
                        Spacer(modifier = Modifier.height(8.dp))
                        TextButton(onClick = {
                            pairingManager.clearPairing()
                            isPaired = false
                        }) {
                            Text("Unpair", color = MaterialTheme.colorScheme.error)
                        }
                    }
                }
            }

            // Connection Settings
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("Connection", style = MaterialTheme.typography.titleMedium)
                    Spacer(modifier = Modifier.height(8.dp))
                    OutlinedTextField(
                        value = manualHost,
                        onValueChange = { manualHost = it },
                        label = { Text("Desktop IP / Hostname") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    OutlinedTextField(
                        value = manualPort,
                        onValueChange = { manualPort = it },
                        label = { Text("Port") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true
                    )
                }
            }

            Spacer(modifier = Modifier.weight(1f))

            // Sync Control Button
            Button(
                onClick = {
                    if (isSyncing) {
                        onStopSync()
                        isSyncing = false
                    } else {
                        onStartSync(
                            manualHost.ifBlank { "192.168.1.1" },
                            manualPort.toIntOrNull() ?: 8765
                        )
                        isSyncing = true
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
