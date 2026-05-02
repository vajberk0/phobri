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

- A random **Data Encryption Key (DEK)** encrypts the SQLite database
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
When locked, the server refuses WebSocket connections with HTTP 423 and
clears all decrypted keys from memory.

## 6. UDP Wake Protocol

Desktop sends a UDP datagram containing the ASCII string `"WAKE"` to the
Android device on port 9876. This signals the Android app to initiate a
WebSocket connection.

**Packet:** 4 bytes: `0x57 0x41 0x4B 0x45` ("WAKE")

## 7. External IP Detection

Desktop fetches external IP from `http://ip.ie.mk/get` and makes it available
to the Android client for off-LAN connectivity.

The service returns JSON: `{"ip": "203.0.113.1"}` or plain text IP.
