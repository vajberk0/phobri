using System.Text.Json.Serialization;

namespace Phobri.Desktop.Models;

/// <summary>
/// Represents a call log entry synchronized from the phone.
/// </summary>
public sealed record CallLogEntry
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("number")]
    public string Number { get; init; } = string.Empty;

    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    [JsonPropertyName("date")]
    public long Date { get; init; }

    [JsonPropertyName("duration")]
    public long Duration { get; init; }

    [JsonPropertyName("type")]
    public CallType Type { get; init; }

    /// <summary>
    /// Display-friendly contact name (falls back to number).
    /// </summary>
    [JsonIgnore]
    public string DisplayName => ContactName ?? Number;

    /// <summary>
    /// Date as DateTimeOffset for display.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset DateTime => DateTimeOffset.FromUnixTimeMilliseconds(Date);

    /// <summary>
    /// Duration formatted as m:ss.
    /// </summary>
    [JsonIgnore]
    public string DurationFormatted
    {
        get
        {
            var minutes = Duration / 60;
            var seconds = Duration % 60;
            return $"{minutes}:{seconds:D2}";
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CallType
{
    Incoming,
    Outgoing,
    Missed,
    Rejected,
    Blocked
}
