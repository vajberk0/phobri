# Phobri Protocol Specification

This document is the normative reference for the Phobri communication protocol
between the Android client and the Desktop server.

## 1. Transport

- **Protocol:** WebSocket over TLS 1.3 (WSS)
- **Server:** Desktop (Avalonia + Kestrel), listens on port 8765
- **Client:** Android (Ktor client), connects to desktop
- **Authentication:** REST endpoints use `X-Phobri-Token` header. WebSocket uses the first message (`pair.init`) for authentication; no HTTP header required for WS upgrade.
- **Certificate:** Self-signed X.509 certificate, TOFU (Trust on First Use)

## 2. REST API (HTTP/1.1)

Base URL: `https://{desktop}:8765/api/v1`

### GET /ping
Health check endpoint.

**Response:**
```json
{
  "status": "ok",
  "version": "1.0.0",
  "paired": true,
  "fingerprint": "a1b2c3...",
  "timestamp": 1714608000000
}
```

### POST /pair/request
Initiate pairing. Returns a pairing token.

**Response:**
```json
{
  "token": "64-char-hex-string",
  "fingerprint": "sha256-fingerprint-of-tls-cert"
}
```

### POST /pair/confirm
Confirm pairing with the token.

**Request:**
```json
{ "token": "64-char-hex-string" }
```

**Response:**
```json
{ "status": "paired" }
```

### GET /sms
Get SMS messages. Query params: `after` (epoch millis), `limit` (default 100).

**Response:**
```json
{
  "messages": [ { ... } ],
  "hasMore": false
}
```

### GET /sms/conversation/{address}
Get conversation with a specific phone number.

### GET /calls
Get call log entries. Query params: `after` (epoch millis), `limit` (default 100).

## 3. WebSocket Protocol

All messages are JSON with this structure. **Enum values use lowercase** (e.g., `"type": "request"`, not `"REQUEST"`). The server uses `System.Text.Json` with `JsonSerializerDefaults.Web` (camelCase, case-insensitive). The Android client uses `kotlinx.serialization` with `@SerialName` annotations for lowercase enum matching.

**Message size:** Keep individual messages under ~100KB. For batch sync, limit to 50 items per message to avoid frame fragmentation issues.

```json
{
  "type": "request|response|push|error",
  "id": "optional-correlation-id",
  "action": "string-identifier",
  "payload": { },
  "error": "error-message-if-type-is-error"
}
```

### Client → Server (Android → Desktop)

| Action | Type | Description |
|--------|------|-------------|
| `pair.init` | request | Initial pairing with token |
| `auth.challenge` | request | Challenge-response auth (nonce + ts) |
| `sms.new` | push | New SMS received on phone |
| `sms.sync` | push | Batch SMS sync |
| `call.new` | push | New call log entry |
| `call.sync` | push | Batch call log sync |
| `fcm.token` | push | FCM token for push notifications |
| `ping` | push | Keep-alive |

### Server → Client (Desktop → Android)

| Action | Type | Description |
|--------|------|-------------|
| `pair.confirmed` | response | Pairing accepted |
| `auth.challenge` | response | Challenge-response HMAC response |
| `sms.sync.request` | request | Request SMS sync |
| `call.sync.request` | request | Request call log sync |
| `sms.send` | request | Send SMS from desktop |
| `pong` | response | Keep-alive response |

### 3.1 Connection Authentication Order

The server enforces authentication before accepting any data push messages.
After the WebSocket connection is established, the client **must** authenticate
via one of two methods before sending `push`-type messages (`sms.new`, `sms.sync`,
`call.new`, `call.sync`, `fcm.token`, `ping`):

1. **`pair.init`** — Send the pairing token. If valid, the connection is authenticated.
   This is used on first pairing and on reconnects when the token is already stored.

2. **`auth.challenge`** — Send a challenge with nonce + timestamp. The server
   responds with the HMAC, proving possession of the SIK (Server Identity Key).
   On success, the connection is authenticated.

Sending a push message before authentication returns an error:
```json
{ "type": "error", "error": "Not authenticated. Send pair.init or auth.challenge first." }
```

## 4. Data Models

### SMS Message

```json
{
  "id": 12345,
  "threadId": 678,
  "address": "+15551234567",
  "contactName": "Mom",
  "body": "Are you coming?",
  "date": 1714608000000,
  "type": "inbox",
  "read": true
}
```

`type` enum: `inbox`, `sent`, `draft`, `outbox`, `failed`

### Call Log Entry

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

`type` enum: `incoming`, `outgoing`, `missed`, `rejected`, `blocked`

## 5. Password-Based Security

Phobri uses a password to encrypt all data at rest and authenticate the server
to the client on each connection.

### 5.1 Envelope Encryption

- A random **Data Encryption Key (DEK)** encrypts the SQLite database at the page level (never decrypted to disk)
- A random **Server Identity Key (SIK)** signs connection challenges
- Both keys are encrypted with a **Key Encryption Key (KEK)** derived from the
  user's password via PBKDF2-HMAC-SHA256 (600,000 iterations)
- AES-256-GCM is used for all symmetric encryption

### 5.2 Challenge-Response Protocol

On each connection, the Android client verifies the desktop server knows the
password-derived SIK:

1. Client generates a random 32-byte nonce and current timestamp
2. Client sends `auth.challenge` request with `nonce` and `ts`
3. Server computes `HMAC-SHA256(SIK, nonce|ts)` and returns the hex-encoded HMAC
4. Client verifies the HMAC against its stored SIK
5. If verification fails, the client disconnects

### 5.3 auth.challenge Payload

**Request:**
```json
{
  "nonce": "64-char-hex-random-nonce",
  "ts": 1714608000000
}
```

**Response:**
```json
{
  "hmac": "hmac-sha256-hex-result"
}
```

### 5.4 Auto-Lock

The desktop app auto-locks after 2 minutes of inactivity (configurable).
When locked:
- The server refuses new WebSocket connections with HTTP 423
- **Existing WebSocket connections are forcibly closed** with close code `PolicyViolation`
- All decrypted keys are cleared from memory
- The database connection is closed

## 6. QR Code Pairing Format

For easy initial setup, the desktop generates a QR code containing all pairing
information. The QR code encodes a URI with the following format:

```
phobri://pair?h=<host>&p=<port>&t=<token>&f=<fingerprint>
```

**Parameters:**

| Param | Description |
|-------|-------------|
| `h`   | Desktop host (IPv4 address or hostname, URL-encoded) |
| `p`   | Server port (integer, default 8765) |
| `t`   | Pairing token (64-char lowercase hex) |
| `f`   | TLS certificate SHA-256 fingerprint (64-char lowercase hex) |

**Example:**
```
phobri://pair?h=192.168.1.5&p=8765&t=a1b2c3d4e5f6...&f=01ab23cd45ef67...
```

When scanned by the Android app, all fields are auto-filled, and the fingerprint
is used directly without needing an initial `/api/v1/ping` round-trip.

## 7. UDP Wake Protocol

Desktop sends a UDP datagram containing the ASCII string `"WAKE"` to the
Android device on port 9876. This signals the Android app to initiate a
WebSocket connection.

**Packet:** 4 bytes: `0x57 0x41 0x4B 0x45` ("WAKE")

## 8. External IP Detection

Desktop fetches external IP from `http://ip.ie.mk/get` and makes it available
to the Android client for off-LAN connectivity.

The service returns JSON: `{"ip": "203.0.113.1"}` or plain text IP.

## 9. FCM Wake Protocol

When the desktop server wants the Android phone to connect (e.g., server IP
changed, phone stopped syncing, or UDP wake failed because the phone is not
on the local network), it sends a high-priority FCM data message.

### 9.1 FCM Token Exchange

After establishing a WebSocket connection and completing authentication, the
Android client sends its FCM registration token to the desktop:

```json
{
  "type": "push",
  "action": "fcm.token",
  "payload": {
    "token": "<fcm-registration-token>"
  }
}
```

The desktop stores this token persistently. When the token is refreshed on
the Android side (e.g., app reinstall), the new token is sent automatically.

### 9.2 FCM Wake Data Message

Desktop → FCM → Android. This is a Firebase Cloud Messaging data message
(not a WebSocket message).

**Data payload** (key-value string pairs):

| Key | Value |
|-----|-------|
| `type` | `"wake"` |
| `serverHost` | Current server hostname or IP address |
| `serverPort` | Server WSS port as string (e.g., `"8765"`) |

**AndroidConfig:** `priority: "high"`, `ttl: 0s` (deliver now or never).

### 9.3 Wake Behavior on Android

When the Android device receives a wake message:

1. `FcmReceiver.onMessageReceived()` is called (works even in background)
2. If the device is not paired, the wake is ignored
3. If the server host has changed from the stored one, it's updated
4. `SyncForegroundService` is started with the new host/port
5. The service connects to the desktop with standard authentication
   (challenge-response via SIK)

### 9.4 Desktop Prerequisites

- Firebase project with Cloud Messaging API enabled
- Service account JSON key file downloaded from Firebase Console
- The service account key path is configured in the desktop Settings tab
- This is optional — Phobri works fine without FCM for LAN-only use
