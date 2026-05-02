using System.Text.Json.Serialization;

namespace Phobri.Desktop.Models;

/// <summary>
/// Represents an SMS message synchronized from the phone.
/// </summary>
public sealed record SmsMessage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("threadId")]
    public long ThreadId { get; init; }

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("date")]
    public long Date { get; init; }

    [JsonPropertyName("type")]
    public SmsType Type { get; init; }

    [JsonPropertyName("read")]
    public bool Read { get; init; }

    /// <summary>
    /// Display-friendly contact name (falls back to address).
    /// </summary>
    [JsonIgnore]
    public string DisplayName => ContactName ?? Address;

    /// <summary>
    /// Date as DateTimeOffset for display.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset DateTime => DateTimeOffset.FromUnixTimeMilliseconds(Date);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmsType
{
    Inbox,
    Sent,
    Draft,
    Outbox,
    Failed
}
