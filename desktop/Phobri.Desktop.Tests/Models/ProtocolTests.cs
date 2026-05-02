using Xunit;
using Phobri.Desktop.Models;
using System.Text.Json;

namespace Phobri.Desktop.Tests.Models;

public sealed class ProtocolTests
{
    [Fact]
    public void SmsMessage_SerializesAndDeserializes()
    {
        var original = new SmsMessage
        {
            Id = 12345,
            ThreadId = 678,
            Address = "+15551234567",
            ContactName = "Mom",
            Body = "Are you coming for dinner?",
            Date = 1714608000000,
            Type = SmsType.Inbox,
            Read = true
        };

        var json = JsonSerializer.Serialize(original, JsonContext.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<SmsMessage>(json, JsonContext.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Address, deserialized.Address);
        Assert.Equal(original.Body, deserialized.Body);
        Assert.Equal(original.Type, deserialized.Type);
    }

    [Fact]
    public void CallLogEntry_SerializesAndDeserializes()
    {
        var original = new CallLogEntry
        {
            Id = 789,
            Number = "+15551234567",
            ContactName = "Mom",
            Date = 1714608000000,
            Duration = 120,
            Type = CallType.Incoming
        };

        var json = JsonSerializer.Serialize(original, JsonContext.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<CallLogEntry>(json, JsonContext.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Number, deserialized.Number);
        Assert.Equal(original.Duration, deserialized.Duration);
        Assert.Equal(original.Type, deserialized.Type);
    }

    [Fact]
    public void ProtocolMessage_Request_Serializes()
    {
        var msg = ProtocolMessage.Request("sms.sync.request", new { after = 0, limit = 100 });

        var json = msg.ToJson();
        var deserialized = ProtocolMessage.FromJson(json);

        Assert.Equal(MessageType.Request, deserialized.Type);
        Assert.Equal("sms.sync.request", deserialized.Action);
        Assert.NotNull(deserialized.Id);
    }

    [Fact]
    public void ProtocolMessage_Push_Serializes()
    {
        var sms = new SmsMessage
        {
            Id = 1,
            Address = "+123",
            Body = "Hello",
            Date = 123456789,
            Type = SmsType.Inbox,
            Read = false
        };

        var msg = ProtocolMessage.Push("sms.new", sms);
        var json = msg.ToJson();

        Assert.Contains("\"sms.new\"", json);
        Assert.Contains("\"Hello\"", json);
    }

    [Fact]
    public void SmsMessage_DisplayName_FallsBackToAddress()
    {
        var withContact = new SmsMessage { Address = "+123", ContactName = "Bob" };
        Assert.Equal("Bob", withContact.DisplayName);

        var withoutContact = new SmsMessage { Address = "+123", ContactName = null };
        Assert.Equal("+123", withoutContact.DisplayName);
    }

    [Fact]
    public void CallLogEntry_DurationFormatted()
    {
        var call = new CallLogEntry { Duration = 125 };
        Assert.Equal("2:05", call.DurationFormatted);

        var zeroCall = new CallLogEntry { Duration = 0 };
        Assert.Equal("0:00", zeroCall.DurationFormatted);
    }
}
