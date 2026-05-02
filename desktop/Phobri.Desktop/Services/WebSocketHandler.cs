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
    private WebSocket? _currentSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Rate limiting for auth attempts
    private int _failedAuthAttempts = 0;
    private DateTime _lastAuthAttempt = DateTime.MinValue;
    private static readonly TimeSpan AuthRateLimitWindow = TimeSpan.FromMinutes(1);
    private const int MaxAuthAttemptsPerWindow = 5;

    public WebSocketHandler(IDataService dataService, IPairingService pairingService)
    {
        _dataService = dataService;
        _pairingService = pairingService;
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
        Console.WriteLine("[WS] Connection handler started");
        _currentSocket = webSocket;
        ConnectionStateChanged?.Invoke(this, true);

        try
        {
            var buffer = new byte[64 * 1024]; // 64KB buffer

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
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
        finally
        {
            Console.WriteLine("[WS] Connection handler ended");
            _currentSocket = null;
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
                    await HandlePushAsync(msg, ct);
                    break;

                case MessageType.Response:
                    await HandleResponseAsync(msg, ct);
                    break;

                case MessageType.Request:
                    await HandleRequestAsync(msg, ct);
                    break;

                case MessageType.Error:
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
                    if (sms is not null)
                    {
                        await _dataService.UpsertSmsAsync(sms);
                        SmsReceived?.Invoke(this, sms);
                    }
                }
                break;

            case "call.new":
                if (msg.Payload.HasValue)
                {
                    var call = JsonSerializer.Deserialize<CallLogEntry>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    if (call is not null)
                    {
                        await _dataService.UpsertCallAsync(call);
                        CallReceived?.Invoke(this, call);
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
                        await _dataService.UpsertSmsBatchAsync(sync.Messages);
                        foreach (var sms in sync.Messages)
                            SmsReceived?.Invoke(this, sms);

                        // Update sync timestamp if this was a batch
                        if (sync.Messages.Count > 0)
                        {
                            var maxDate = sync.Messages.Max(m => m.Date);
                            await _dataService.SetLastSyncTimestampAsync("sms", maxDate);
                        }
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
                        await _dataService.UpsertCallBatchAsync(sync.Calls);
                        foreach (var call in sync.Calls)
                            CallReceived?.Invoke(this, call);

                        if (sync.Calls.Count > 0)
                        {
                            var maxDate = sync.Calls.Max(c => c.Date);
                            await _dataService.SetLastSyncTimestampAsync("calls", maxDate);
                        }
                    }
                }
                break;

            case "fcm.token":
                if (msg.Payload.HasValue)
                {
                    var fcmPayload = JsonSerializer.Deserialize<FcmTokenPayload>(
                        msg.Payload.Value.GetRawText(),
                        JsonContext.DefaultOptions);
                    // Store FCM token if needed
                    if (fcmPayload?.Token is { Length: > 0 })
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Received FCM token: {fcmPayload.Token[..8]}...");
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
        // Rate limit auth attempts
        if ((DateTime.UtcNow - _lastAuthAttempt) > AuthRateLimitWindow)
        {
            _failedAuthAttempts = 0;
        }
        _lastAuthAttempt = DateTime.UtcNow;

        if (_failedAuthAttempts >= MaxAuthAttemptsPerWindow)
        {
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Too many authentication attempts. Try again later.", msg.Id), ct);
            return;
        }

        var sik = _pairingService.ServerIdentityKey;
        if (sik is null)
        {
            _failedAuthAttempts++;
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
            _failedAuthAttempts++;
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Missing nonce in auth challenge.", msg.Id), ct);
            return;
        }

        // Validate timestamp is within 5 minutes to prevent replay
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - timestamp) > 5 * 60 * 1000)
        {
            _failedAuthAttempts++;
            await SendMessageAsync(
                ProtocolMessage.ErrorMessage("Challenge timestamp expired.", msg.Id), ct);
            return;
        }

        // Compute HMAC-SHA256(nonce || timestamp, SIK)
        var message = $"{nonce}|{timestamp}";
        var hmac = CryptoService.ComputeHmacSha256Hex(sik, message);

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
                Console.WriteLine($"[pair.init] ConfirmPairing result: {confirmed}");
            }

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
}
