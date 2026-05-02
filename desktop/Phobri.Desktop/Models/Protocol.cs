using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phobri.Desktop.Models;

/// <summary>
/// Base protocol message for WebSocket communication.
/// </summary>
public sealed record ProtocolMessage
{
    [JsonPropertyName("type")]
    public MessageType Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// Create a request message.
    /// </summary>
    public static ProtocolMessage Request(string action, object? payload = null, string? id = null)
        => new()
        {
            Type = MessageType.Request,
            Action = action,
            Id = id ?? Guid.NewGuid().ToString("N")[..8],
            Payload = payload is not null
                ? JsonSerializer.SerializeToElement(payload, JsonContext.DefaultOptions)
                : null
        };

    /// <summary>
    /// Create a response message.
    /// </summary>
    public static ProtocolMessage Response(string action, object? payload = null, string? id = null)
        => new()
        {
            Type = MessageType.Response,
            Action = action,
            Id = id,
            Payload = payload is not null
                ? JsonSerializer.SerializeToElement(payload, JsonContext.DefaultOptions)
                : null
        };

    /// <summary>
    /// Create a push notification message.
    /// </summary>
    public static ProtocolMessage Push(string action, object payload)
        => new()
        {
            Type = MessageType.Push,
            Action = action,
            Payload = JsonSerializer.SerializeToElement(payload, JsonContext.DefaultOptions)
        };

    /// <summary>
    /// Create an error message.
    /// </summary>
    public static ProtocolMessage ErrorMessage(string message, string? id = null)
        => new()
        {
            Type = MessageType.Error,
            Error = message,
            Id = id
        };

    /// <summary>
    /// Serialize to JSON string.
    /// </summary>
    public string ToJson()
        => JsonSerializer.Serialize(this, JsonContext.DefaultOptions);

    /// <summary>
    /// Deserialize from JSON string.
    /// </summary>
    public static ProtocolMessage FromJson(string json)
        => JsonSerializer.Deserialize<ProtocolMessage>(json, JsonContext.DefaultOptions)
           ?? throw new JsonException("Failed to deserialize ProtocolMessage");
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    Request,
    Response,
    Push,
    Error
}

/// <summary>
/// Payload for sms.sync action.
/// </summary>
public sealed record SmsSyncPayload
{
    [JsonPropertyName("messages")]
    public List<SmsMessage> Messages { get; init; } = [];

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// Payload for call.sync action.
/// </summary>
public sealed record CallSyncPayload
{
    [JsonPropertyName("calls")]
    public List<CallLogEntry> Calls { get; init; } = [];

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// Payload for sms.send request.
/// </summary>
public sealed record SendSmsRequest
{
    [JsonPropertyName("phoneNumbers")]
    public List<string> PhoneNumbers { get; init; } = [];

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Payload for sync requests.
/// </summary>
public sealed record SyncRequest
{
    [JsonPropertyName("after")]
    public long? After { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>
/// Payload for ping/pong.
/// </summary>
public sealed record PingPayload
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

/// <summary>
/// Payload for FCM token exchange.
/// </summary>
public sealed record FcmTokenPayload
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}

/// <summary>
/// Payload for auth.challenge request (phone → desktop).
/// </summary>
public sealed record AuthChallengePayload
{
    [JsonPropertyName("nonce")]
    public string Nonce { get; init; } = string.Empty;

    [JsonPropertyName("ts")]
    public long Timestamp { get; init; }
}

/// <summary>
/// Payload for auth.challenge response (desktop → phone).
/// </summary>
public sealed record AuthChallengeResponsePayload
{
    [JsonPropertyName("hmac")]
    public string Hmac { get; init; } = string.Empty;
}

/// <summary>
/// Centralized JSON serialization context for source-gen perf.
/// </summary>
[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(SmsMessage))]
[JsonSerializable(typeof(CallLogEntry))]
[JsonSerializable(typeof(SmsSyncPayload))]
[JsonSerializable(typeof(CallSyncPayload))]
[JsonSerializable(typeof(SendSmsRequest))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(PingPayload))]
[JsonSerializable(typeof(FcmTokenPayload))]
[JsonSerializable(typeof(AuthChallengePayload))]
[JsonSerializable(typeof(AuthChallengeResponsePayload))]
[JsonSerializable(typeof(JsonElement))]
public partial class JsonContext : JsonSerializerContext
{
    /// <summary>
    /// Default options used throughout the app.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
