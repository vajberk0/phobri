using Xunit;
using Phobri.Desktop.Services;
using Phobri.Desktop.Models;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Tests.Services;

/// <summary>
/// Tests for DataService with encrypted storage.
/// Database must be unlocked with a DEK before operations.
/// </summary>
public sealed class DataServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DataService _service;
    private readonly byte[] _testDek;

    public DataServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"phobri-test-{Guid.NewGuid():N}.db");
        _testDek = CryptoService.GenerateKey();
        _service = new DataService(_dbPath);
    }

    private async Task UnlockAsync()
    {
        await _service.UnlockAsync(_testDek);
    }

    [Fact]
    public async Task Unlock_CreatesAndDecryptsDatabase()
    {
        await UnlockAsync();
        Assert.True(_service.IsOpen);
    }

    [Fact]
    public async Task UpsertSms_InsertsMessage()
    {
        await UnlockAsync();

        var msg = new SmsMessage
        {
            Id = 1,
            ThreadId = 10,
            Address = "+1234567890",
            ContactName = "Test",
            Body = "Hello world",
            Date = 1714608000000,
            Type = SmsType.Inbox,
            Read = true
        };

        await _service.UpsertSmsAsync(msg);

        var messages = await _service.GetSmsMessagesAsync();
        Assert.Single(messages);
        Assert.Equal("Hello world", messages[0].Body);
    }

    [Fact]
    public async Task UpsertSms_UpdatesExistingMessage()
    {
        await UnlockAsync();

        var msg = new SmsMessage
        {
            Id = 1,
            ThreadId = 10,
            Address = "+123",
            Body = "Original",
            Date = 1000,
            Type = SmsType.Inbox,
            Read = false
        };

        await _service.UpsertSmsAsync(msg);

        // Update the same message
        var updated = msg with { Body = "Updated", Read = true };
        await _service.UpsertSmsAsync(updated);

        var messages = await _service.GetSmsMessagesAsync();
        Assert.Single(messages);
        Assert.Equal("Updated", messages[0].Body);
        Assert.True(messages[0].Read);
    }

    [Fact]
    public async Task UpsertSmsBatch_InsertsMultiple()
    {
        await UnlockAsync();

        var messages = new List<SmsMessage>
        {
            new() { Id = 1, Address = "+1", Body = "A", Date = 1000, Type = SmsType.Inbox, Read = true },
            new() { Id = 2, Address = "+1", Body = "B", Date = 2000, Type = SmsType.Sent, Read = true },
            new() { Id = 3, Address = "+2", Body = "C", Date = 3000, Type = SmsType.Inbox, Read = false }
        };

        await _service.UpsertSmsBatchAsync(messages);

        var results = await _service.GetSmsMessagesAsync();
        Assert.Equal(3, results.Count);
        Assert.Equal(3000, results[0].Date); // Sorted DESC
    }

    [Fact]
    public async Task GetSmsMessages_WithAfterFilter()
    {
        await UnlockAsync();

        var messages = new List<SmsMessage>
        {
            new() { Id = 1, Address = "+1", Body = "Old", Date = 1000, Type = SmsType.Inbox, Read = true },
            new() { Id = 2, Address = "+1", Body = "New", Date = 2000, Type = SmsType.Inbox, Read = true }
        };

        await _service.UpsertSmsBatchAsync(messages);

        var results = await _service.GetSmsMessagesAsync(after: 1500);
        Assert.Single(results);
        Assert.Equal("New", results[0].Body);
    }

    [Fact]
    public async Task GetConversation_FiltersByAddress()
    {
        await UnlockAsync();

        var messages = new List<SmsMessage>
        {
            new() { Id = 1, Address = "+Alice", Body = "Hello", Date = 1000, Type = SmsType.Inbox, Read = true },
            new() { Id = 2, Address = "+Bob", Body = "Hi", Date = 2000, Type = SmsType.Inbox, Read = true },
            new() { Id = 3, Address = "+Alice", Body = "World", Date = 3000, Type = SmsType.Sent, Read = true }
        };

        await _service.UpsertSmsBatchAsync(messages);

        var aliceMessages = await _service.GetConversationAsync("+Alice");
        Assert.Equal(2, aliceMessages.Count);
        Assert.All(aliceMessages, m => Assert.Equal("+Alice", m.Address));
    }

    [Fact]
    public async Task UpsertCall_InsertsEntry()
    {
        await UnlockAsync();

        var call = new CallLogEntry
        {
            Id = 1,
            Number = "+123",
            ContactName = "Test",
            Date = 1714608000000,
            Duration = 60,
            Type = CallType.Incoming
        };

        await _service.UpsertCallAsync(call);

        var calls = await _service.GetCallLogAsync();
        Assert.Single(calls);
        Assert.Equal(CallType.Incoming, calls[0].Type);
    }

    [Fact]
    public async Task SyncState_TracksTimestamp()
    {
        await UnlockAsync();

        var initial = await _service.GetLastSyncTimestampAsync("sms");
        Assert.Equal(0, initial);

        await _service.SetLastSyncTimestampAsync("sms", 1714608000000);
        var updated = await _service.GetLastSyncTimestampAsync("sms");
        Assert.Equal(1714608000000, updated);
    }

    [Fact]
    public async Task LockAndUnlock_PreservesData()
    {
        await UnlockAsync();

        // Insert some data
        var msg = new SmsMessage
        {
            Id = 1,
            Address = "+123",
            Body = "Test lock/unlock",
            Date = 1000,
            Type = SmsType.Inbox,
            Read = true
        };
        await _service.UpsertSmsAsync(msg);

        // Lock
        await _service.LockAsync(_testDek);
        Assert.False(_service.IsOpen);

        // Unlock again
        await _service.UnlockAsync(_testDek);
        Assert.True(_service.IsOpen);

        // Data should still be there
        var messages = await _service.GetSmsMessagesAsync();
        Assert.Single(messages);
        Assert.Equal("Test lock/unlock", messages[0].Body);
    }

    [Fact]
    public async Task OperationsWithoutUnlock_ThrowsException()
    {
        // Don't unlock
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.GetSmsMessagesAsync());
    }

    public void Dispose()
    {
        try
        {
            _service.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch { /* best effort */ }
    }
}
