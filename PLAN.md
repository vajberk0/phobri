# Phobri — Architecture & Implementation Plan

> **Phobri** = **Pho**ne **Bri**dge. Cross-device SMS and call log synchronization
> between an Android phone and a desktop computer.

---

## 1. System Architecture

```
┌────────────────────────────────┐        ┌─────────────────────────────────┐
│     ANDROID APP (Kotlin)       │        │     DESKTOP APP (C# / Avalonia 12) │
│                                │        │                                  │
│  Foreground Service            │        │  Avalonia MVVM GUI               │
│  ┌──────────────────────────┐  │  WSS   │  ┌────────────────────────────┐  │
│  │ WebSocket CLIENT ────────┼──┼───────►│  │ Kestrel WebSocket Server   │  │
│  │ (Ktor client)            │  │        │  │ (ports 8765 WSS, 8764 HTTP) │  │
│  └──────────────────────────┘  │        │  └────────────────────────────┘  │
│                                │        │                                  │
│  ┌──────────────────────────┐  │  UDP   │  ┌────────────────────────────┐  │
│  │ UDP Wake Listener ───────┼──◄───────│  │ UDP Wake Sender            │  │
│  │ (DatagramSocket, port    │  │        │  │ (sends WAKE packet)        │  │
│  │  9876)                   │  │        │  └────────────────────────────┘  │
│  └──────────────────────────┘  │        │                                  │
│                                │        │  ┌────────────────────────────┐  │
│  ┌──────────────────────────┐  │        │  │ SQLite Local Cache         │  │
│  │ ContentObserver (SMS)    │  │        │  │ (offline access)           │  │
│  │ PhoneStateListener       │  │        │  └────────────────────────────┘  │
│  │ (push new data)          │  │        │                                  │
│  └──────────────────────────┘  │        │  External IP detection           │
│                                │        │  (http://ip.ie.mk/get)           │
│  ┌──────────────────────────┐  │        │                                  │
│  │ FCM Message Receiver ────┼──◄──FCM──│  FCM Push Sender (optional)      │
│  │ (fallback wake)          │  │        │                                  │
│  └──────────────────────────┘  │        │                                  │
└────────────────────────────────┘        └─────────────────────────────────┘
```

### Communication Flow

1. **Discovery & Pairing (one-time):**
   - Desktop shows a pairing QR code or code
   - Android scans/enters it, connects via WebSocket
   - TLS cert fingerprint exchanged (TOFU)
   - FCM token exchanged (optional)
   - Pairing key stored on both sides

2. **Normal operation (LAN):**
   - Android maintains WebSocket connection to desktop
   - New SMS/call → Android pushes immediately over WebSocket
   - Desktop queries (sync history, send SMS) → sent over WebSocket
   - Desktop sends periodic UDP keep-alive

3. **Wake ladder (if Android disconnected):**
   - Desktop tries UDP wake packet first
   - Falls back to FCM high-priority push
   - Android connects back to desktop

4. **Off-LAN operation:**
   - Desktop fetches external IP from `http://ip.ie.mk/get`
   - Desktop includes external IP in pairing info
   - Android uses external IP when not on same LAN
   - User must set up port forwarding on their router (8764, 8765)

---

## 2. Communication Protocol

### 2.1 Transport Security

- **TLS 1.3** with self-signed certificate
- **Trust-on-first-use (TOFU):** On first pairing, desktop generates a self-signed cert.
  Android stores the cert's SHA-256 fingerprint. On subsequent connections, Android
  verifies the fingerprint matches. If mismatch → alert user.
- **Authentication:** Pairing token (random 32-byte hex string) sent as HTTP header
  `X-Phobri-Token` on every request.

### 2.2 WebSocket Message Format

All messages are JSON with a type discriminator:

```json
{
  "type": "request|response|push|error",
  "id": "optional-correlation-id",
  "action": "sms.sync|call.sync|sms.send|pair.request|...",
  "payload": { }
}
```

### 2.3 REST Endpoints (Desktop Kestrel Server)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/ping` | Health check, returns server info + external IP |
| `POST` | `/api/v1/pair/request` | Initiate pairing (first connection) |
| `POST` | `/api/v1/pair/confirm` | Confirm pairing with token |
| `GET` | `/api/v1/sms?after={ts}&limit={n}` | Get SMS messages (paginated by date) |
| `GET` | `/api/v1/sms/conversation/{number}` | Get conversation thread |
| `GET` | `/api/v1/calls?after={ts}&limit={n}` | Get call log entries |
| `POST` | `/api/v1/sms/send` | Send SMS from the computer |

### 2.4 WebSocket Actions

**Android → Desktop (client pushes):**

| Action | Description |
|--------|-------------|
| `sms.new` | New SMS received on phone |
| `sms.sync` | Batch SMS sync response |
| `call.new` | New call log entry |
| `call.sync` | Batch call log sync response |
| `pair.init` | Initial pairing request |
| `ping` | Keep-alive |

**Desktop → Android (server requests):**

| Action | Description |
|--------|-------------|
| `sms.sync.request` | Request full/incremental SMS sync |
| `call.sync.request` | Request full/incremental call log sync |
| `sms.send` | Request to send an SMS |
| `pair.challenge` | Pairing challenge |
| `fcm.token.request` | Request FCM token |
| `pong` | Keep-alive response |

---

## 3. Data Models

### 3.1 SMS Message (shared)

```json
{
  "id": 12345,
  "threadId": 678,
  "address": "+15551234567",
  "contactName": "Mom",
  "body": "Are you coming for dinner?",
  "date": 1714608000000,
  "type": "inbox",
  "read": true
}
```

`type` values: `inbox`, `sent`, `draft`, `outbox`, `failed`

### 3.2 Call Log Entry (shared)

```json
{
  "id": 789,
  "number": "+15551234567",
  "contactName": "Mom",
  "date": 1714608000000,
  "duration": 120,
  "type": "incoming"
}
```

`type` values: `incoming`, `outgoing`, `missed`, `rejected`, `blocked`

---

## 4. Android App Design

### 4.1 Technology Stack
- **Language:** Kotlin
- **Min SDK:** 26 (Android 8.0)
- **Target SDK:** 34 (Android 14)
- **UI:** Jetpack Compose
- **HTTP/WS Client:** Ktor Client
- **Serialization:** kotlinx.serialization
- **Async:** Kotlin Coroutines + Flow
- **DI:** Manual (or Koin if needed)

### 4.2 Key Components

| Component | Purpose |
|-----------|---------|
| `MainActivity` | Single activity, hosts Compose UI |
| `SyncService` | Foreground service, runs WebSocket client + UDP listener |
| `SmsReader` | Reads SMS via `ContentResolver` |
| `CallLogReader` | Reads call log via `ContentResolver` |
| `SmsObserver` | `ContentObserver` for real-time SMS detection |
| `CallObserver` | `PhoneStateListener` for call state changes |
| `WebSocketClient` | Ktor WebSocket client with auto-reconnect |
| `UdpWakeListener` | `DatagramSocket` listener on port 9876 |
| `PairingManager` | TOFU cert pinning + pairing token storage |
| `FcmReceiver` | `FirebaseMessagingService` for FCM wake (optional) |

### 4.3 Permissions
- `READ_SMS`
- `RECEIVE_SMS`
- `SEND_SMS` (only if send-from-desktop is implemented)
- `READ_CALL_LOG`
- `READ_PHONE_STATE`
- `READ_CONTACTS` (optional, for contact name resolution)
- `FOREGROUND_SERVICE`
- `FOREGROUND_SERVICE_DATA_SYNC`
- `INTERNET`
- `ACCESS_NETWORK_STATE`
- `ACCESS_WIFI_STATE`
- `RECEIVE_BOOT_COMPLETED`
- `POST_NOTIFICATIONS` (Android 13+)

---

## 5. Desktop App Design

### 5.1 Technology Stack
- **Language:** C# (.NET 10)
- **UI Framework:** Avalonia 12
- **MVVM Toolkit:** CommunityToolkit.Mvvm
- **HTTP Server:** ASP.NET Core Kestrel (embedded)
- **WebSocket Server:** ASP.NET Core WebSocket middleware
- **Database:** SQLite via Microsoft.Data.Sqlite
- **Serialization:** System.Text.Json
- **HTTP Client:** HttpClient (IHttpClientFactory)
- **Testing:** xUnit 3

### 5.2 Key Components

| Component | Purpose |
|-----------|---------|
| `SyncServer` | Kestrel server hosting WebSocket + REST API |
| `UdpWakeService` | Sends UDP wake packets to phone |
| `ExternalIpService` | Fetches external IP from `ip.ie.mk` |
| `PairingService` | Manages TOFU pairing, generates self-signed certs |
| `DataService` | SQLite CRUD for local SMS/call log cache |
| `WebSocketHandler` | Handles WebSocket connections from phone |
| `FcmPushService` | Sends FCM pushes via Firebase Admin SDK (optional) |
| `MainViewModel` | Main window VM: connection status, sync controls |
| `SmsViewModel` | SMS list VM with conversation grouping |
| `CallLogViewModel` | Call log list VM |

### 5.3 UI Layout

```
┌──────────────────────────────────────────────────────────────┐
│  📱 Phobri                         [Connected ✅] [⚙ Settings] │
├──────────────┬───────────────────────────────────────────────┤
│  📨 Messages │  ┌───────────────────────────────────────────┐ │
│  📞 Calls    │  │ Conversation / Detail Panel               │ │
│              │  │                                           │ │
│  ─────────── │  │  Mom                                      │ │
│  Contacts    │  │  Are you coming for dinner?          2:30 │ │
│  Mom      ●  │  │  Yes, on my way! 🚗                 2:31 │ │
│  Bob         │  │                                           │ │
│  Alice       │  └───────────────────────────────────────────┘ │
│  +123456     │                                               │
├──────────────┴───────────────────────────────────────────────┤
│  Status: Synced 42 messages, 15 calls  │  Last sync: 14:30   │
└──────────────────────────────────────────────────────────────┘
```

---

## 6. Security Design

### 6.1 TLS Certificate
- Desktop generates self-signed X.509 cert on first launch
- Cert saved to `~/.phobri/server.pfx`
- Android pins the cert's SHA-256 fingerprint on pairing
- On reconnect, Android validates fingerprint matches

### 6.2 Pairing Flow
1. Desktop generates a pairing token (32 random hex bytes)
2. Desktop shows token as QR code and text
3. User enters token on Android (or scans QR)
4. Android connects via WSS and sends `pair.init` with token
5. Desktop validates token, stores pairing
6. Android stores cert fingerprint + pairing token + desktop addresses

### 6.3 Data at Rest
- Desktop SQLite database stored in `~/.phobri/data.db`
- No encryption at rest (OS-level disk encryption is assumed)
- Pairing tokens stored in `~/.phobri/config.json`

---

## 7. Project Structure

```
phobri/
├── PLAN.md                          # This file
├── README.md                        # User-facing documentation
├── AGENTS.md                        # Instructions for future AI sessions
│
├── android/                         # Android Kotlin app
│   ├── build.gradle.kts             # Root build file
│   ├── settings.gradle.kts
│   ├── gradle.properties
│   ├── gradle/
│   │   └── libs.versions.toml       # Version catalog
│   └── app/
│       ├── build.gradle.kts
│       └── src/
│           ├── main/
│           │   ├── AndroidManifest.xml
│           │   └── kotlin/com/phobri/android/
│           │       ├── PhobriApplication.kt
│           │       ├── MainActivity.kt
│           │       ├── service/
│           │       │   ├── SyncForegroundService.kt
│           │       │   └── UdpWakeListener.kt
│           │       ├── sync/
│           │       │   ├── SmsReader.kt
│           │       │   ├── CallLogReader.kt
│           │       │   ├── SmsObserver.kt
│           │       │   ├── CallObserver.kt
│           │       │   └── PhobriWebSocketClient.kt
│           │       ├── pairing/
│           │       │   ├── PairingManager.kt
│           │       │   └── CertificatePinner.kt
│           │       ├── model/
│           │       │   ├── SmsMessage.kt
│           │       │   ├── CallLogEntry.kt
│           │       │   └── Protocol.kt
│           │       ├── network/
│           │       │   └── IpDetector.kt
│           │       └── ui/
│           │           ├── theme/
│           │           ├── screen/
│           │           │   ├── MainScreen.kt
│           │           │   ├── PairingScreen.kt
│           │           │   └── SettingsScreen.kt
│           │           └── component/
│           └── test/
│               └── kotlin/com/phobri/android/
│                   ├── sync/
│                   │   ├── SmsReaderTest.kt
│                   │   └── CallLogReaderTest.kt
│                   ├── pairing/
│                   │   └── PairingManagerTest.kt
│                   └── model/
│                       └── ProtocolTest.kt
│
├── desktop/                         # C# Avalonia desktop app
│   ├── Phobri.Desktop/
│   │   ├── Phobri.Desktop.csproj
│   │   ├── Program.cs
│   │   ├── App.axaml
│   │   ├── App.axaml.cs
│   │   ├── MainWindow.axaml
│   │   ├── MainWindow.axaml.cs
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── SmsViewModel.cs
│   │   │   ├── CallLogViewModel.cs
│   │   │   └── PairingViewModel.cs
│   │   ├── Models/
│   │   │   ├── SmsMessage.cs
│   │   │   ├── CallLogEntry.cs
│   │   │   └── Protocol.cs
│   │   ├── Services/
│   │   │   ├── SyncServer.cs
│   │   │   ├── WebSocketHandler.cs
│   │   │   ├── UdpWakeService.cs
│   │   │   ├── ExternalIpService.cs
│   │   │   ├── PairingService.cs
│   │   │   ├── DataService.cs
│   │   │   └── IFcmPushService.cs
│   │   ├── Data/
│   │   │   └── PhobriDbContext.cs
│   │   └── Infrastructure/
│   │       ├── TlsCertificateGenerator.cs
│   │       └── ConfigurationManager.cs
│   │
│   ├── Phobri.Desktop.Tests/
│   │   ├── Phobri.Desktop.Tests.csproj
│   │   ├── Services/
│   │   │   ├── ExternalIpServiceTests.cs
│   │   │   ├── UdpWakeServiceTests.cs
│   │   │   ├── PairingServiceTests.cs
│   │   │   ├── DataServiceTests.cs
│   │   │   └── TlsCertificateGeneratorTests.cs
│   │   └── Models/
│   │       └── ProtocolTests.cs
│   │
│   └── Phobri.Desktop.IntegrationTests/
│       ├── Phobri.Desktop.IntegrationTests.csproj
│       └── FullProtocolTests.cs      # E2E protocol tests (TLS, WS, REST)
│
├── phobri_test.sh                    # Unified test runner script
│
└── shared/
    └── protocol.md                  # Protocol documentation (normative)
```

---

## 8. Implementation Order

### Phase 1: Foundation & Protocol
1. Create project scaffolding for both apps
2. Define shared data models and protocol types
3. Desktop: basic Avalonia app with empty MVVM shell
4. Android: basic Compose app with permission requests

### Phase 2: Desktop Server
5. Implement TLS certificate generation (`TlsCertificateGenerator`)
6. Implement Kestrel `SyncServer` with WebSocket endpoint
7. Implement `WebSocketHandler` - accept connections, parse messages
8. Implement `DataService` - SQLite schema, CRUD operations
9. Write tests for server, WS handler, data service

### Phase 3: Android Client
10. Implement `SmsReader` and `CallLogReader` with ContentResolver
11. Implement `PhobriWebSocketClient` with Ktor
12. Implement `SmsObserver` (ContentObserver) and `CallObserver`
13. Implement `UdpWakeListener`
14. Implement `SyncForegroundService`
15. Write tests for readers, client

### Phase 4: Pairing & Security
16. Implement `PairingService` (desktop)
17. Implement `PairingManager` + `CertificatePinner` (Android)
18. Implement pairing UI on both sides
19. Write pairing tests

### Phase 5: GUI Polish
20. Desktop: SMS list view with conversation grouping
21. Desktop: Call log view
22. Desktop: Settings panel, connection status
23. Android: Main screen with server status
24. Android: Settings screen

### Phase 6: Advanced Features
25. UDP wake ladder (desktop side)
26. FCM integration (both sides, optional)
27. External IP detection for off-LAN support
28. SMS sending from desktop
29. MMS support (basic)

### Phase 7: Documentation
30. Write README.md
31. Write AGENTS.md
32. Final testing pass

---

## 9. Testing Strategy

### Android
- **Unit tests:** `SmsReader`, `CallLogReader`, `PairingManager`, JSON serialization
- **Mock ContentResolver** using Robolectric or manual fakes
- **Integration tests:** WebSocket client against a test server

### Desktop
- **Unit tests:** All services, ViewModels (`desktop/Phobri.Desktop.Tests/`)
- **Integration tests:** Full protocol E2E — spin up headless server, connect
  simulated Android client, exercise TLS, WebSocket, pairing, SMS/call sync,
  REST endpoints (`desktop/Phobri.Desktop.IntegrationTests/`)
- **Headless server mode:** `dotnet run -- --headless` runs the Kestrel server
  without GUI, usable for CI, automated testing, and headless VMs
- **View tests:** ViewModel binding correctness

### Shared Protocol
- JSON roundtrip tests on both sides
- Edge cases: empty messages, long messages, special characters, null fields

### CI / Automated Testing
- `./phobri_test.sh` runs all tests (desktop unit + integration + android unit)
- `./phobri_test.sh --quick` runs desktop-only tests (fastest)
- Android emulator is pre-configured (AVD: `phobri_test`, android-35, x86_64)
  but without KVM hardware acceleration it is too slow for CI; use the
  integration test project instead for automated protocol verification

---

## 10. Key Dependencies

### Android
| Library | Version | Purpose |
|---------|---------|---------|
| Ktor Client | 3.x | WebSocket/HTTP client |
| kotlinx.serialization | 1.7.x | JSON |
| Jetpack Compose BOM | 2024.x | UI |
| AndroidX Core KTX | 1.15.x | Android extensions |
| AndroidX Lifecycle | 2.8.x | ViewModel, service lifecycle |

### Desktop
| Library | Version | Purpose |
|---------|---------|---------|
| Avalonia | 12.x | UI framework |
| Avalonia.Themes.Fluent | 12.x | Fluent theme |
| CommunityToolkit.Mvvm | 8.4.x | MVVM toolkit |
| Microsoft.Data.Sqlite | 9.x | SQLite |
| Microsoft.AspNetCore.Server.Kestrel | 9.x | Embedded HTTP/WS server |
| xUnit | 3.x | Testing |
