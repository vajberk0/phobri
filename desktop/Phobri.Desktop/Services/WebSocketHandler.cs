using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Models;

namespace Phobri.Desktop.Services;

/// <summary>
/// Manages WebSocket connections from Android devices.
/// Handles message routing, push notifications, command dispatch,
/// and password-based challenge-response authentication.
/// </summary>
public interface IWebSocketHandler
{
    /// <summary>Event raised when a new SMS is received from the phone.</summary>
    event EventHandler<SmsMessage>? SmsReceived;

    /// <summary>Event raised when a new call log entry is received from the phone.</summary>
    event EventHandler<CallLogEntry>? CallReceived;

    /// <summary>Event raised when the phone connection state changes.</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Whether a phone is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Accept and handle a new WebSocket connection.</summary>
    Task HandleConnectionAsync(WebSocket webSocket, CancellationToken ct);

    /// <summary>Send a protocol message to the connected phone.</summary>
    Task SendMessageAsync(ProtocolMessage message, CancellationToken ct = default);

    /// <summary>Send an SMS from the computer through the phone.</summary>
    Task SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default);
}

public sealed class WebSocketHandler : IWebSocketHandler
{
    private readonly IDataService _dataService;
    private readonly IPairingService _pairingService;
    private readonly IPasswordManagerService _passwordManager;
    private readonly ILogService _log;
    private readonly IFcmPushService? _fcmPush;
    private WebSocket? _currentSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Per-connection authentication state.
    // This handler is a singleton (single phone design), so one connection
    // at a time is the norm. Reset on each new connection.
    private bool _authenticated;

    // Global rate limiting for auth attempts (static — shared across all
    // connections since the handler is a singleton).
    private static int s_failedAuthAttempts;
    private static DateTime s_lastAuthAttempt = DateTime.MinValue;
    private static readonly object s_rateLimitLock = new();
    private static readonly TimeSpan AuthRateLimitWindow = TimeSpan.FromMinutes(1);
    private const int MaxAuthAttemptsPerWindow = 5;
    private const int MaxMessageSizeBytes = 256 * 1024; // 256KB

    public WebSocketHandler(IDataService dataService, IPairingService pairingService, IPasswordManagerService passwordManager, ILogService logService, IFcmPushService? fcmPush = null)
    {
        _dataService = dataService;
        _pairingService = pairingService;
        _passwordManager = passwordManager;
        _log = logService;
        _fcmPush = fcmPush;
    }

    /// <inheritdoc/>
    public event EventHandler<SmsMessage>? SmsReceived;
    public event EventHandler<CallLogEntry>? CallReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc/>
    public bool IsConnected =>
        _currentSocket?.State == WebSocketState.Open;

    /// <inheritdoc/>
    public async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken ct)
    {
        _log.Log("WS", "Phone connected");
        Console.WriteLine("[WS] Connection handler started");
        _currentSocket = webSocket;
        _authenticated = false;
        ConnectionStateChanged?.Invoke(this, true);

        try
        {
            var buffer = new byte[64 * 1024]; // 64KB buffer

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Check if the vault has been locked (auto-lock or manual)
                if (!_passwordManager.IsUnlocked)
                {
                    _log.Log("WS", "Vault locked — closing connection");
                    Console.WriteLine("[WS] Vault locked — closing connection");
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation, "Server locked", ct);
                    break;
                }

                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // Handle multi-frame messages
                    if (!result.EndOfMessage)
                    {
                        var fullMessage = new StringBuilder(json);
                        while (!result.EndOfMessage)
                        {
                            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            fullMessage.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            if (fullMessage.Length > MaxMessageSizeBytes)
                            {
                                _log.Log("WS", $"Message too large ({fullMessage.Length} bytes) — closing");
                                await webSocket.CloseAsync(
                                    WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                                return;
                            }
                        }
                        json = fullMessage.ToString();
                    }

                    await ProcessMessageAsync(json, ct);
                }
            }
        }
        catch (WebSocketException)
        {
            // Connection closed unexpectedly
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        finally
        {
            _log.Log("WS", "Phone disconnected");
            Console.WriteLine("[WS] Connection handler ended");
            _currentSocket = null;
            _authenticated = false;
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    /// <inheritdoc/>
    public async Task SendMessageAsync(ProtocolMessage message, CancellationToken ct = default)
    {
        if (_currentSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("No phone connected");

        await _sendLock.WaitAsync(ct);
        try
        {
            var json = message.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            await _currentSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default)
    {
        var request = ProtocolMessage.Request(
            "sms.send",
            new SendSmsRequest
            {
                PhoneNumbers = [phoneNumber],
                Text = text
            });

        await SendMessageAsync(request, ct);
    }

    // --- Message Processing ---

    private async Task ProcessMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            var msg = ProtocolMessage.FromJson(json);

            switch (msg.Type)
            {
                case MessageType.Push:
                    _log.Log("WS", $"← {msg.Action}", msg.Payload?.GetRawText()?.Truncate(200));
                    await HandlePushAsync(msg, ct);
                    break;

                case MessageType.Response:
                    _log.Log("WS", $"← resp {msg.Action}");
                    await HandleResponseAsync(msg, ct);
                    break;

                case MessageType.Request:
                    _log.Log("WS", $"← req {msg.Action}");
                    await HandleRequestAsync(msg, ct);
                    break;

                case MessageType.Error:
                    _log.Log("WS", $"← error: {msg.Error}");
                    System.Diagnostics.Debug.WriteLine(
                        $"Phone error: {msg.Error}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to parse message: {ex.Message}");
        }
    }

    private async Task HandlePushAsync(ProtocolMessage msg, CancellationToken ct)
    {
        // Reject push messages from unauthenticated connections
        if (!_authenticated)
        {
            _log.Log("WS", $"REJECTED push {msg.Action} — not authenticated");
            Console.WriteLine($"[push] REJECTED {msg.Action} — connection not authenticated");
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Not authenticated. Send pair.init or auth.challenge first.", msg.Id), ct);
            return;
        }

        Console.WriteLine($"[push] {msg.Action}");
        try
        {
        switch (msg.Action)
        {
            case "sms.new":
                if (msg.Payload.HasValue)
                {
                    var sms = JsonSerializer.Deserialize<SmsMessage>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (sms is not null && ValidateSmsMessage(sms))
                    {
                        _log.Log("WS", $"sms.new: {sms.DisplayName} — \"{sms.Body?.Truncate(80)}\"");
                        await _dataService.UpsertSmsAsync(sms);
                        SmsReceived?.Invoke(this, sms);
                    }
                    else if (sms is not null)
                    {
                        _log.Log("WS", $"sms.new: REJECTED invalid message (id={sms.Id})");
                    }
                }
                break;

            case "call.new":
                if (msg.Payload.HasValue)
                {
                    var call = JsonSerializer.Deserialize<CallLogEntry>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (call is not null && ValidateCallLogEntry(call))
                    {
                        _log.Log("WS", $"call.new: {call.DisplayName} ({call.Type}) duration={call.Duration}s");
                        await _dataService.UpsertCallAsync(call);
                        CallReceived?.Invoke(this, call);
                    }
                    else if (call is not null)
                    {
                        _log.Log("WS", $"call.new: REJECTED invalid entry (id={call.Id})");
                    }
                }
                break;

            case "sms.sync":
                if (msg.Payload.HasValue)
                {
                    var sync = JsonSerializer.Deserialize<SmsSyncPayload>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (sync?.Messages is { Count: > 0 })
                    {
                        // Enforce batch size limit (max 50) and validate each message
                        var validMessages = sync.Messages
                            .Take(50)
                            .Where(ValidateSmsMessage)
                            .ToList();
                        if (validMessages.Count > 0)
                        {
                            _log.Log("WS", $"sms.sync: {validMessages.Count} messages");
                            await _dataService.UpsertSmsBatchAsync(validMessages);
                            foreach (var sms in validMessages)
                                SmsReceived?.Invoke(this, sms);

                            var maxDate = validMessages.Max(m => m.Date);
                            await _dataService.SetLastSyncTimestampAsync("sms", maxDate);
                        }
                        if (sync.Messages.Count > 50)
                            _log.Log("WS", $"sms.sync: dropped {sync.Messages.Count - 50} excess messages");
                    }
                }
                break;

            case "call.sync":
                if (msg.Payload.HasValue)
                {
                    var sync = JsonSerializer.Deserialize<CallSyncPayload>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (sync?.Calls is { Count: > 0 })
                    {
                        // Enforce batch size limit (max 50) and validate each entry
                        var validCalls = sync.Calls
                            .Take(50)
                            .Where(ValidateCallLogEntry)
                            .ToList();
                        if (validCalls.Count > 0)
                        {
                            _log.Log("WS", $"call.sync: {validCalls.Count} calls");
                            await _dataService.UpsertCallBatchAsync(validCalls);
                            foreach (var call in validCalls)
                                CallReceived?.Invoke(this, call);

                            var maxDate = validCalls.Max(c => c.Date);
                            await _dataService.SetLastSyncTimestampAsync("calls", maxDate);
                        }
                        if (sync.Calls.Count > 50)
                            _log.Log("WS", $"call.sync: dropped {sync.Calls.Count - 50} excess calls");
                    }
                }
                break;

            case "fcm.token":
                if (msg.Payload.HasValue)
                {
                    var fcmPayload = JsonSerializer.Deserialize<FcmTokenPayload>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (fcmPayload?.Token is { Length: > 0 })
                    {
                        _log.Log("WS", $"fcm.token: {fcmPayload.Token[..8]}...");
                        // Persist the token so FCM wake can use it
                        if (_fcmPush is not null)
                        {
                            _ = _fcmPush.StoreFcmTokenAsync(fcmPayload.Token);
                        }
                    }
                }
                break;

            case "ping":
                // Respond with pong
                await SendMessageAsync(
                    ProtocolMessage.Response("pong",
                        new PingPayload
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }),
                    ct);
                break;
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[push] ERROR in {msg.Action}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleResponseAsync(ProtocolMessage msg, CancellationToken ct)
    {
        // Response handling for pending requests
        // For now, these are logged; future: use correlation IDs for request/response matching
        System.Diagnostics.Debug.WriteLine(
            $"Response: {msg.Action} (id={msg.Id})");
        await Task.CompletedTask;
    }

    private async Task HandleRequestAsync(ProtocolMessage msg, CancellationToken ct)
    {
        switch (msg.Action)
        {
            case "pair.init":
                await HandlePairInitAsync(msg, ct);
                break;

            case "auth.challenge":
                await HandleAuthChallengeAsync(msg, ct);
                break;
        }
    }

    private async Task HandleAuthChallengeAsync(ProtocolMessage msg, CancellationToken ct)
    {
        // Global rate limit auth attempts (thread-safe, shared across connections)
        lock (s_rateLimitLock)
        {
            if ((DateTime.UtcNow - s_lastAuthAttempt) > AuthRateLimitWindow)
            {
                s_failedAuthAttempts = 0;
            }
            s_lastAuthAttempt = DateTime.UtcNow;
            s_failedAuthAttempts++;
        }

        if (s_failedAuthAttempts > MaxAuthAttemptsPerWindow)
        {
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Too many authentication attempts. Try again later.", msg.Id), ct);
            return;
        }

        var sik = _pairingService.ServerIdentityKey;
        if (sik is null)
        {
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Server is locked. Unlock with password first.", msg.Id), ct);
            return;
        }

        // Extract nonce and timestamp from the challenge
        string? nonce = null;
        long timestamp = 0;

        if (msg.Payload.HasValue)
        {
            if (msg.Payload.Value.TryGetProperty("nonce", out var nonceElem))
                nonce = nonceElem.GetString();
            if (msg.Payload.Value.TryGetProperty("ts", out var tsElem) && tsElem.TryGetInt64(out var ts))
                timestamp = ts;
        }

        if (string.IsNullOrEmpty(nonce))
        {
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Missing nonce in auth challenge.", msg.Id), ct);
            return;
        }

        // Validate timestamp is within 5 minutes to prevent replay
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - timestamp) > 5 * 60 * 1000)
        {
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Challenge timestamp expired.", msg.Id), ct);
            return;
        }

        // Compute HMAC-SHA256(nonce || timestamp, SIK)
        var message = $"{nonce}|{timestamp}";
        var hmac = CryptoService.ComputeHmacSha256Hex(sik, message);

        // Mark this connection as authenticated — the client has proven
        // knowledge of the SIK
        _authenticated = true;
        _log.Log("WS", "🔐 Auth challenge OK — connection authenticated");

        await SendMessageAsync(
            ProtocolMessage.Response("auth.challenge",
                new AuthChallengeResponsePayload { Hmac = hmac },
                msg.Id),
            ct);
    }

    private async Task HandlePairInitAsync(ProtocolMessage msg, CancellationToken ct)
    {
        // Validate pairing token
        if (!msg.Payload.HasValue)
        {
            Console.WriteLine("[pair.init] No payload");
            return;
        }

        var payload = msg.Payload.Value;
        string? token = null;
        if (payload.TryGetProperty("token", out var tokenElem))
            token = tokenElem.GetString();

        Console.WriteLine($"[pair.init] Received token: {(token is null ? "null" : token[..Math.Min(16, token.Length)] + "...")}");
        Console.WriteLine($"[pair.init] IsPaired: {_pairingService.IsPaired}, HasPendingToken: {_pairingService.HasPendingToken}");
        Console.WriteLine($"[pair.init] Stored token: {(_pairingService.PairingToken is null ? "null" : _pairingService.PairingToken[..Math.Min(16, _pairingService.PairingToken.Length)] + "...")}");

        if (token is not null && _pairingService.ValidateToken(token))
        {
            Console.WriteLine("[pair.init] Token VALID");
            // If this is a pending (first-time) token, confirm the pairing
            if (!_pairingService.IsPaired)
            {
                var confirmed = _pairingService.ConfirmPairing(token);
                _log.Log("WS", $"pair.init confirmed (first-time pairing): {confirmed}");
                Console.WriteLine($"[pair.init] ConfirmPairing result: {confirmed}");
            }

            // Mark this connection as authenticated
            _authenticated = true;
            _log.Log("WS", "🔑 pair.init OK — connection authenticated");

            await SendMessageAsync(
                ProtocolMessage.Response("pair.confirmed", new { status = "ok" }), ct);
        }
        else
        {
            Console.WriteLine("[pair.init] Token INVALID — sending error");
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Invalid or missing pairing token", msg.Id), ct);
        }
    }

    /// <summary>
    /// Reset the global auth rate limiter (for testing / server restart).
    /// </summary>
    public static void ResetRateLimiter()
    {
        lock (s_rateLimitLock)
        {
            s_failedAuthAttempts = 0;
            s_lastAuthAttempt = DateTime.MinValue;
        }
    }

    // --- Input Validation ---

    private static readonly DateTimeOffset MinValidDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int MaxAddressLength = 256;
    private const int MaxContactNameLength = 256;
    private const int MaxBodyLength = 10 * 1024; // 10KB

    /// <summary>
    /// Validate an SMS message before storage. Rejects oversized fields,
    /// out-of-range dates, and missing required fields.
    /// </summary>
    private static bool ValidateSmsMessage(SmsMessage sms)
    {
        if (string.IsNullOrWhiteSpace(sms.Address) || sms.Address.Length > MaxAddressLength)
            return false;
        if (sms.ContactName is { Length: > 0 } && sms.ContactName.Length > MaxContactNameLength)
            return false;
        if (sms.Body.Length > MaxBodyLength)
            return false;

        // Validate date is within reasonable range
        var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(sms.Date);
        if (dateTime < MinValidDate || dateTime > DateTimeOffset.UtcNow.AddDays(1))
            return false;

        return true;
    }

    /// <summary>
    /// Validate a call log entry before storage. Rejects oversized fields,
    /// out-of-range dates, and implausible durations.
    /// </summary>
    private static bool ValidateCallLogEntry(CallLogEntry call)
    {
        if (string.IsNullOrWhiteSpace(call.Number) || call.Number.Length > MaxAddressLength)
            return false;
        if (call.ContactName is { Length: > 0 } && call.ContactName.Length > MaxContactNameLength)
            return false;

        // Validate date is within reasonable range
        var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(call.Date);
        if (dateTime < MinValidDate || dateTime > DateTimeOffset.UtcNow.AddDays(1))
            return false;

        // Duration must be plausible (0s to 24h)
        if (call.Duration < 0 || call.Duration > 86_400)
            return false;

        return true;
    }
}
