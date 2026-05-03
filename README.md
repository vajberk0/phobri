# Phobri — Phone Bridge

> **Phobri** = **Pho**ne **Bri**dge

Cross-device SMS and call log synchronization between Android and Desktop.

## Features

- 📨 **SMS Sync** — Read and view SMS messages from your phone on your desktop
- 📞 **Call Log Sync** — View incoming, outgoing, and missed calls
- ⚙️ **Configurable Sync** — Toggle SMS/call log sync independently, set max batch size
- 🔄 **Real-time Push** — New SMS and calls appear instantly on desktop
- 🔒 **Encrypted** — TLS 1.3 with certificate pinning (TOFU)
- 🏠 **LAN & Remote** — Works on local Wi-Fi and over the internet (with port forwarding)
- 📱 **Android-native** — Kotlin + Jetpack Compose UI
- 🖥️ **Cross-platform Desktop** — Windows, macOS, Linux via Avalonia 12 + .NET 10

## Architecture

```
┌──────────────────┐         WSS + REST        ┌──────────────────────┐
│  Android (Kotlin) │ ◄───────────────────────► │  Desktop (C# / .NET) │
│  WebSocket Client │                           │  Kestrel Server      │
│  ContentResolver  │                           │  Avalonia 12 GUI     │
│  UDP Wake Listener│  ◄── UDP WAKE ────────── │  SQLite Cache        │
│  FCM Receiver     │  ◄── FCM push ────────── │  FCM Push Sender     │
└──────────────────┘                           └──────────────────────┘
```

The **desktop runs the server**. The Android connects as a WebSocket client.
This avoids running a server on the phone (better battery, no open ports on phone).

See [PLAN.md](PLAN.md) for the full architecture and implementation plan.
See [shared/protocol.md](shared/protocol.md) for the communication protocol specification.

## Project Structure

```
phobri/
├── PLAN.md              # Architecture & implementation plan
├── README.md            # This file
├── AGENTS.md            # Instructions for AI coding agents
├── shared/
│   └── protocol.md      # Protocol specification
├── desktop/
│   ├── Phobri.Desktop/           # Avalonia 12 desktop app
│   │   ├── Models/               # Data models (SMS, CallLog, Protocol)
│   │   ├── Services/             # SyncServer, WebSocket, Pairing, Data
│   │   ├── ViewModels/           # MVVM view models
│   │   ├── Views/                # Avalonia XAML views
│   │   └── Infrastructure/       # TLS certs, config
│   └── Phobri.Desktop.Tests/     # xUnit tests
└── android/
    └── app/
        ├── src/main/kotlin/com/phobri/android/
        │   ├── service/          # Foreground sync service, UDP listener
        │   ├── sync/             # SMS/Call readers, WS client, observers
        │   ├── pairing/          # TOFU pairing, cert pinning
        │   ├── model/            # Data models
        │   ├── network/          # IP detection
        │   └── ui/               # Jetpack Compose UI
        └── src/test/             # Unit tests
```

## Getting Started

### Prerequisites

- **Desktop:** .NET 10 SDK
- **Android:** Android Studio, JDK 17, Android SDK 26+

### Desktop App

#### GUI Mode

```bash
cd desktop/Phobri.Desktop
dotnet run
```

The app starts an Avalonia window with a WebSocket server on port 8765.
Click "Start Server" to begin listening for phone connections.

#### Headless Server Mode

For servers, headless VMs, or automated testing:

```bash
cd desktop/Phobri.Desktop
dotnet run -- --headless
# or with custom port:
dotnet run -- --headless --port 9000
```

This skips the GUI entirely, starts the Kestrel server with TLS, and prints
the pairing token, certificate fingerprint, and listen addresses.
Press Ctrl+C to stop.

### Android App

1. Open `android/` in Android Studio
2. Build and install on your device
3. Grant SMS, Call Log, and Phone permissions
4. **Option A (recommended):** Tap "Scan QR Code" and scan the QR code shown on desktop
   → all pairing fields are auto-filled
5. **Option B:** Manually enter the pairing token and desktop IP address shown on desktop
6. Configure sync: toggle SMS/call log sync and set max entries per batch
7. Tap "Start Sync"

### Pairing Flow

1. Desktop generates a pairing token and displays it as a QR code + text
2. **QR route:** Android scans the QR → host, port, token, and cert fingerprint auto-filled
3. **Manual route:** Enter token + host + port manually on Android
4. Android connects via WSS and authenticates with the token
5. Android pins the desktop's TLS certificate fingerprint
6. Subsequent connections verify the fingerprint

### Off-LAN Access

1. Desktop fetches its external IP from `http://ip.ie.mk/get`
2. Forward ports 8765 (WSS) and 8766 (HTTP) on your router to the desktop
3. On Android, enter the external IP as the host

### FCM Push-to-Wake (Optional)

FCM enables the desktop to wake the phone and tell it the current server
address — useful when the server IP changes (Tailscale, DHCP) or the phone
sync service has stopped.

**Setup:**

1. Create a Firebase project at https://console.firebase.google.com/
2. Add an Android app with package name `com.phobri.android`
3. Download `google-services.json` and place it in `android/app/` (copy `android/app/google-services.json.example` → `google-services.json` as a starting point)
4. In Firebase Console → Project Settings → Service Accounts, generate a new private key (JSON)
5. On the desktop, go to Settings → FCM Configuration and enter the path to the JSON key file
6. Click "Configure FCM" — status should show "✓ FCM ready"

Once configured, clicking "Wake Phone" on desktop will try both UDP (LAN) and
FCM push (internet). FCM also auto-wakes the phone when the server's external
IP changes.

## Running Tests

### Quick: All tests via script

```bash
./phobri_test.sh           # Desktop unit + integration + Android unit
./phobri_test.sh --quick   # Desktop unit + integration only (fast)
./phobri_test.sh --unit-only
```

### Desktop Unit Tests

```bash
cd desktop/Phobri.Desktop.Tests
dotnet test
```

### Desktop Integration Tests (full protocol)

Starts an ephemeral headless server and tests the entire Android↔Desktop
protocol — TLS, WebSocket messages, SMS/call sync, REST endpoints — without
needing an Android device or emulator.

```bash
cd desktop/Phobri.Desktop.IntegrationTests
dotnet test
```

Covers: ping/pong, pairing, SMS push, SMS batch sync, call push, call batch
sync, server-to-client requests, multi-frame messages, REST queries.

### Android Unit Tests (Robolectric, no emulator needed)

```bash
cd android
./gradlew test
```

### Android Emulator (optional, for manual E2E testing)

```bash
# One-time setup:
export ANDROID_HOME=$HOME/android-sdk
sdkmanager --sdk_root=$ANDROID_HOME "emulator" "system-images;android-35;google_apis;x86_64"
echo "no" | avdmanager create avd -n "phobri_test" \
  -k "system-images;android-35;google_apis;x86_64" -d "pixel_8" --force

# Start (headless):
emulator -avd phobri_test -no-window -no-audio -no-boot-anim \
  -accel off -gpu swiftshader_indirect -memory 2048

# Install the app:
cd android && ./gradlew assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell pm grant com.phobri.android android.permission.READ_SMS
adb shell pm grant com.phobri.android android.permission.READ_CALL_LOG
adb shell pm grant com.phobri.android android.permission.READ_PHONE_STATE
adb shell pm grant com.phobri.android android.permission.POST_NOTIFICATIONS
```

> ⚠️ Without KVM hardware acceleration, the emulator cold boot takes ~5 minutes.
> Snapshot boots take ~20s but snapshots can get corrupted. For automated E2E
> testing, prefer the integration tests above.

## Key Technology Stack

| Component | Technology |
|-----------|-----------|
| Desktop UI | Avalonia 12 (.NET 10) |
| Desktop MVVM | CommunityToolkit.Mvvm |
| Desktop Server | ASP.NET Core Kestrel |
| Desktop DB | SQLite (Microsoft.Data.Sqlite) |
| Android UI | Jetpack Compose |
| Android HTTP/WS | Ktor Client |
| Android Serialization | kotlinx.serialization |
| Security | TLS 1.3 + Certificate Pinning (TOFU) |

## Security

- **TLS 1.3** for all communication
- **TOFU (Trust on First Use)** certificate pinning
- **Pairing token** authentication (32-byte random hex)
- No sensitive data stored in cloud — all data stays on your devices
- Desktop certificate stored in `~/.phobri/server.pfx`
- Local SQLite database stored in `~/.phobri/data.db`

## Google Play Policy

Phobri qualifies for the **"Cross-device synchronization or transfer of SMS or calls"**
exception under Google Play's SMS/Call Log permission policy. A Permissions Declaration
Form must be submitted through Google Play Console.

The recommended distribution method is APK sideloading to avoid Play Store policy friction.

## License

MIT
