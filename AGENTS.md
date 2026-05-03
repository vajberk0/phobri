# AGENTS.md — AI Coding Agent Instructions

This file provides context and instructions for AI coding agents working on the
Phobri project. Read this first before making any changes.

## Project Overview

Phobri is a two-app system for syncing SMS and call logs from Android to Desktop.

- **Android app:** Kotlin + Jetpack Compose + Ktor client → connects as WebSocket client
- **Desktop app:** C# + Avalonia 12 + Kestrel → runs WebSocket/HTTP server
- **Communication:** WSS (TLS 1.3) with TOFU certificate pinning
- **Repository:** https://github.com/vajberk0/phobri

## Key Documents

| File | Purpose |
|------|---------|
| `PLAN.md` | Full architecture, technology decisions, roadmap |
| `README.md` | User-facing documentation |
| `shared/protocol.md` | Normative protocol specification |
| `AGENTS.md` | This file — instructions for AI agents |

## Development Rules

### General
1. **Read PLAN.md first** — it has the definitive architecture
2. **Keep tests passing** — run `dotnet test` after desktop changes and `./gradlew test` after Android changes
3. **Protocol changes must update `shared/protocol.md`**
4. **Both apps must agree on JSON field names** — use camelCase
5. **All JSON uses camelCase** — `kotlinx.serialization` and `System.Text.Json` both configured

### Desktop (C# / Avalonia 12)
- Project: `desktop/Phobri.Desktop/`
- Unit tests: `desktop/Phobri.Desktop.Tests/`
- Integration tests: `desktop/Phobri.Desktop.IntegrationTests/` (full protocol E2E)
- Build: `dotnet build`
- Test: `dotnet test`
- Target: `.NET 10`, Avalonia 12.0.2
- **MVVM pattern** via `CommunityToolkit.Mvvm` (source generators)
- **Compiled bindings** enabled by default in Avalonia 12
- Kestrel server runs **embedded** in the Avalonia app
- **Headless mode:** `dotnet run -- --headless [--port N] [--password pw] [--password-file path] [--fcm-key-path path]`
- Data stored in `~/.phobri/` (config, SQLite db, TLS cert)
- **DI container** configured in `App.axaml.cs` (GUI) and `Program.ConfigureHeadlessServices()` (headless)
- **Namespaces:** `Phobri.Desktop.Models`, `.Services`, `.ViewModels`, `.Views`, `.Infrastructure`
- Auto-lock: set `AutoLockMinutes: 0` in `~/.phobri/config.json` to disable

### FCM Push Setup
- Requires Firebase project at https://console.firebase.google.com/
- Android: replace `android/app/google-services.json` with real one
- Desktop: set service account key path in Settings → FCM Configuration
- Service account key JSON: download from Firebase Console → Project Settings → Service Accounts
- FCM is optional — Phobri works fine without it for LAN-only use

### Android (Kotlin)
- Project: `android/app/`
- Min SDK: 26, Target SDK: 35
- Build: `./gradlew assembleDebug` (requires Android SDK)
- Test: `./gradlew test`
- **Jetpack Compose** for UI with real-time event log and copyable errors
- **Ktor 3.x** with **OkHttp engine** for WebSocket client (not CIO — hostname verification)
- **kotlinx.serialization** for JSON — enums use `@SerialName` lowercase to match server
- `SyncForegroundService` manages WebSocket connection with non-blocking connect/reconnect
- UDP wake listener on port 9876
- `ContentObserver` for real-time SMS detection
- `PhoneStateListener` for call state changes
- **Package:** `com.phobri.android`
- **Namespaces:** `com.phobri.android.model`, `.sync`, `.service`, `.pairing`, `.network`, `.ui`
- Version auto-increments on every build (timestamp-based versionCode/versionName)

## Communication Protocol

### REST Endpoints (Desktop server)
| Method | Path | Auth |
|--------|------|------|
| GET | `/api/v1/ping` | No |
| GET | `/api/v1/auth/status` | No |
| POST | `/api/v1/pair/request` | No |
| POST | `/api/v1/pair/confirm` | No |
| GET | `/api/v1/sms?after=&limit=` | Header `X-Phobri-Token` + unlocked |
| GET | `/api/v1/sms/conversation/{address}` | Header + unlocked |
| GET | `/api/v1/calls?after=&limit=` | Header + unlocked |

### WebSocket Actions
| Direction | Action | Description |
|-----------|--------|-------------|
| Android→Desktop | `pair.init` | Pairing with token (sent as first WS message; not an HTTP header) |
| Android→Desktop | `auth.challenge` | Challenge-response auth (nonce + ts) |
| Android→Desktop | `sms.new` | New SMS push |
| Android→Desktop | `sms.sync` | Batch SMS sync (send ≤50 per message to avoid frame size issues) |
| Android→Desktop | `call.new` | New call push |
| Android→Desktop | `call.sync` | Batch call sync |
| Android→Desktop | `fcm.token` | FCM token push |
| Android→Desktop | `ping` | Keep-alive |
| Desktop→Android | `pair.confirmed` | Pairing accepted |
| Desktop→Android | `auth.challenge` | Challenge-response HMAC answer |
| Desktop→Android | `sms.sync.request` | Request sync |
| Desktop→Android | `call.sync.request` | Request sync |
| Desktop→Android | `sms.send` | Send SMS |
| Desktop→Android | `pong` | Keep-alive |

### Data Models (both apps must match)
SMS: `id`, `threadId`, `address`, `contactName`, `body`, `date`, `type`, `read`
Call: `id`, `number`, `contactName`, `date`, `duration`, `type`

Auth Challenge: `nonce` (64-char hex), `ts` (epoch millis), `hmac` (64-char hex)

## Security
- TLS 1.3 self-signed cert generated by desktop on first launch
- SHA-256 fingerprint pinned by Android on pairing (TOFU)
- Pairing token: 32 random bytes as hex (64 chars)
- **WebSocket auth enforcement:** Server rejects all push messages (`sms.new`, `sms.sync`,
  `call.new`, `call.sync`, `fcm.token`, `ping`) from unauthenticated connections.
  Client must send `pair.init` (with token) or `auth.challenge` before pushing data.
- Auth: WebSocket uses first message (`pair.init`) for auth; REST uses `X-Phobri-Token` header
- Self-signed cert hostname verification disabled for dev/Tailscale (authenticity verified via pinned fingerprint)
- **Auto-lock** defaults to 2 minutes; set `AutoLockMinutes: 0` in config to disable.
  When auto-lock fires, existing WebSocket connections are forcibly closed and
  new connections receive HTTP 423.

## Known TODOs & Future Work
- [x] ~~Pairing loop bug — fixed with pending token mechanism~~
- [x] ~~Android CIO engine → OkHttp for hostname verification control~~
- [x] Proper TOFU cert pinning for Ktor OkHttp (trust-all for dev → pinned TrustManager)
- [x] Easy server setup in the client via QR code
- [x] FCM integration for reliable wake (optional)
- [ ] Server cert SAN should include Tailscale hostname at generation time
- [ ] SMS sending from desktop
- [ ] Conversation search
- [ ] Export conversations
- [ ] Multiple device pairing

## CI / Headless Testing

The desktop app supports a `--headless` flag that runs the Kestrel server
without any GUI or Avalonia dependency, making it suitable for CI and
headless VMs. Integration tests in `desktop/Phobri.Desktop.IntegrationTests/`
use this to spin up an ephemeral server and test the entire protocol
(TLS, WebSocket, REST, pairing, SMS/call sync) in ~3 seconds without
needing an Android device or emulator.

### Headless server
```bash
cd desktop/Phobri.Desktop && dotnet run -- --headless
```

### Android emulator (headless VM)
An AVD named `phobri_test` (android-35, x86_64, google_apis) is pre-configured.
The emulator can boot without KVM but is slow (~20s snapshot, ~5min cold boot).
Hardware acceleration (KVM) is strongly recommended for regular use.

```bash
export ANDROID_HOME=$HOME/android-sdk
emulator -avd phobri_test -no-window -no-audio -no-boot-anim \
  -accel off -gpu swiftshader_indirect -memory 2048
```

## Android Build Setup (This Machine)

The Android SDK is installed at `~/android-sdk` (not system-wide).
Environment variables required:
```bash
export ANDROID_HOME=$HOME/android-sdk
```
The `local.properties` in `android/` already points to this path.

Components installed:
- Platform: android-35
- Build-tools: 34.0.0
- Platform-tools: 37.0.0

Gradle wrapper (8.11.1) is committed; no system Gradle needed.

## Post-Change Checklist

**After every set of changes, run all of these before considering the work done:**

1. **Build APK** — `cd android && ./gradlew assembleDebug`
2. **Restart server / run all tests** — `./phobri_test.sh` (desktop unit + integration + android unit)
3. **Update markdown files** — AGENTS.md, README.md, PLAN.md, and/or `shared/protocol.md` if protocol changed
4. **Commit and push:**
```bash
git add -A
git commit -m "<descriptive message>"
git push
```

## Test Commands
```bash
# Quick: desktop unit + integration + android unit
./phobri_test.sh

# Desktop unit tests only
cd desktop/Phobri.Desktop.Tests && dotnet test

# Desktop integration tests (full protocol, no Android needed)
cd desktop/Phobri.Desktop.IntegrationTests && dotnet test

# Android test (needs Android SDK)
cd android && ./gradlew test

# Full check (all tests)
cd android && ./gradlew test && cd ../desktop/Phobri.Desktop.Tests && dotnet test && cd ../Phobri.Desktop.IntegrationTests && dotnet test && echo "All: OK"
```

## File Organization Notes

### Desktop key files:
- `Models/Protocol.cs` — ProtocolMessage, SyncPayload, JsonContext, AuthChallenge payloads
- `Models/SmsMessage.cs` — SmsMessage, SmsType
- `Models/CallLogEntry.cs` — CallLogEntry, CallType
- `Infrastructure/CryptoService.cs` — PBKDF2 key derivation, AES-256-GCM, HMAC
- `Services/PasswordManagerService.cs` — Password setup, unlock, auto-lock, envelope encryption
- `Services/SyncServer.cs` — Kestrel embedded server with REST + WS endpoints
- `Services/WebSocketHandler.cs` — WS connection handling, message routing, challenge-response
- `Services/DataService.cs` — SQLite CRUD with page-level encrypted storage (SQLite3MC)
- `Services/PairingService.cs` — TOFU pairing logic with encrypted token storage
- `Services/UdpWakeService.cs` — UDP wake sender
- `Services/ExternalIpService.cs` — ip.ie.mk external IP fetcher
- `Infrastructure/TlsCertificateGenerator.cs` — Self-signed cert generation
- `Infrastructure/ConfigurationManager.cs` — JSON config persistence (encrypted fields)
- `ViewModels/MainWindowViewModel.cs` — Main orchestrator VM
- `ViewModels/SmsViewModel.cs` — SMS list & conversation VM
- `ViewModels/CallLogViewModel.cs` — Call log list VM
- `ViewModels/PairingViewModel.cs` — Pairing/settings VM
- `Views/MainWindow.axaml` — Main window XAML layout
- `App.axaml.cs` — DI container setup

### Password / Security Notes
- Desktop DB is always encrypted at rest at the page level (SQLite3 Multiple Ciphers); never decrypted to disk
- Password must be entered on desktop startup to unlock the vault
- Headless mode accepts `--password <pw>`, `--password-file <path>`, or interactive prompt
- Auto-lock defaults to 2 minutes; configurable in config.json (`AutoLockMinutes`)
- Android stores SIK (Server Identity Key) in EncryptedSharedPreferences (AES-256-GCM, Keystore-backed); received during pairing
- On each connection, Android sends `auth.challenge` to verify server has correct SIK
- Challenge-response: HMAC-SHA256(SIK, nonce|timestamp) with 5-minute timestamp window
- Rate limiting: 5 failed auth attempts per minute per connection

### Android key files:
- `service/SyncForegroundService.kt` — Foreground service with WS + UDP wake + auth
- `sync/PhobriWebSocketClient.kt` — Ktor WebSocket client with challenge-response
- `sync/SmsReader.kt` — ContentResolver SMS reader
- `sync/CallLogReader.kt` — ContentResolver call log reader
- `sync/SmsObserver.kt` — ContentObserver for real-time SMS
- `sync/CallObserver.kt` — PhoneStateListener for calls
- `pairing/PairingManager.kt` — TOFU pairing + cert pinning + SIK storage + sync config (SMS/calls toggles, max entries)
- `model/SmsMessage.kt` — SMS and CallLog data models
- `model/Protocol.kt` — Protocol messages including AuthChallenge
- `network/IpDetector.kt` — Local IP detection
- `ui/screen/MainScreen.kt` — Main Compose UI
- `MainActivity.kt` — Activity + permission handling
- `AndroidManifest.xml` — Permissions + service declaration
