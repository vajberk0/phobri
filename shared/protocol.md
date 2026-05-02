# Phobri Protocol Specification

This document is the normative reference for the Phobri communication protocol
between the Android client and the Desktop server.

## 1. Transport

- **Protocol:** WebSocket over TLS 1.3 (WSS)
- **Server:** Desktop (Avalonia + Kestrel), listens on port 8765
- **Client:** Android (Ktor client), connects to desktop
- **Authentication:** Pairing token sent as initial WebSocket message or HTTP header `X-Phobri-Token`
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

All messages are JSON with this structure:

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

## 5. UDP Wake Protocol

Desktop sends a UDP datagram containing the ASCII string `"WAKE"` to the
Android device on port 9876. This signals the Android app to initiate a
WebSocket connection.

**Packet:** 4 bytes: `0x57 0x41 0x4B 0x45` ("WAKE")

## 6. External IP Detection

Desktop fetches external IP from `http://ip.ie.mk/get` and makes it available
to the Android client for off-LAN connectivity.

The service returns JSON: `{"ip": "203.0.113.1"}` or plain text IP.
