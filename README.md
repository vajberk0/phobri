# Phobri — Phone Bridge

> **Phobri** = **Pho**ne **Bri**dge

Cross-device SMS and call log synchronization between Android and Desktop.

## Features

- 📨 **SMS Sync** — Read and view SMS messages from your phone on your desktop
- 📞 **Call Log Sync** — View incoming, outgoing, and missed calls
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
│  UDP Wake Listener│                           │  SQLite Cache        │
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

```bash
cd desktop/Phobri.Desktop
dotnet run
```

The app starts an Avalonia window with a WebSocket server on port 8765.
Click "Start Server" to begin listening for phone connections.

### Android App

1. Open `android/` in Android Studio
2. Build and install on your device
3. Grant SMS, Call Log, and Phone permissions
4. Enter the pairing token shown on the desktop
5. Enter the desktop's IP address
6. Tap "Start Sync"

### Pairing Flow

1. Desktop generates a pairing token
2. Enter the token on the Android app
3. Android connects via WSS and authenticates with the token
4. Android pins the desktop's TLS certificate fingerprint
5. Subsequent connections verify the fingerprint

### Off-LAN Access

1. Desktop fetches its external IP from `http://ip.ie.mk/get`
2. Forward ports 8765 (WSS) and 8766 (HTTP) on your router to the desktop
3. On Android, enter the external IP as the host

## Running Tests

### Desktop

```bash
cd desktop/Phobri.Desktop.Tests
dotnet test
```

### Android

```bash
cd android
./gradlew test
```

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
