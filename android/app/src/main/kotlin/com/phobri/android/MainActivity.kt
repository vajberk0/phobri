package com.phobri.android

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import com.phobri.android.service.SyncForegroundService
import com.phobri.android.ui.screen.MainScreen
import com.phobri.android.ui.theme.PhobriTheme

class MainActivity : ComponentActivity() {

    private val requiredPermissions = mutableListOf(
        Manifest.permission.READ_SMS,
        Manifest.permission.RECEIVE_SMS,
        Manifest.permission.READ_CALL_LOG,
        Manifest.permission.READ_PHONE_STATE,
        Manifest.permission.READ_CONTACTS
    ).apply {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            add(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        val allGranted = permissions.values.all { it }
        if (allGranted) {
            // All permissions granted, UI can proceed
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Request permissions on first launch
        if (!hasRequiredPermissions()) {
            permissionLauncher.launch(requiredPermissions.toTypedArray())
        }

        setContent {
            PhobriTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    MainScreen(
                        onStartSync = { host, port ->
                            startSyncService(host, port)
                        },
                        onStopSync = {
                            stopSyncService()
                        },
                        onPermissionsRequested = {
                            permissionLauncher.launch(requiredPermissions.toTypedArray())
                        }
                    )
                }
            }
        }
    }

    private fun hasRequiredPermissions(): Boolean {
        return requiredPermissions.all { permission ->
            ContextCompat.checkSelfPermission(this, permission) ==
                    PackageManager.PERMISSION_GRANTED
        }
    }

    private fun startSyncService(host: String, port: Int) {
        val intent = Intent(this, SyncForegroundService::class.java).apply {
            action = SyncForegroundService.ACTION_START
            putExtra(SyncForegroundService.EXTRA_DESKTOP_HOST, host)
            putExtra(SyncForegroundService.EXTRA_DESKTOP_PORT, port)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopSyncService() {
        val intent = Intent(this, SyncForegroundService::class.java).apply {
            action = SyncForegroundService.ACTION_STOP
        }
        startService(intent)
    }
}
