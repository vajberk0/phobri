using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Models;
using Phobri.Desktop.Services;
using Xunit;

namespace Phobri.Desktop.IntegrationTests;

/// <summary>
/// End-to-end integration tests that start the headless server,
/// connect a simulated Android client, and exercise the full protocol.
/// </summary>
public sealed class FullProtocolTests : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    private SyncServer _server = null!;
    private IPairingService _pairing = null!;
    private string _pairingToken = null!;
    private string _certFingerprint = null!;
    private int _port;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    public async ValueTask InitializeAsync()
    {
        // Use a unique port to avoid conflicts
        _port = 18765 + Random.Shared.Next(1000);

        // Set up services with a temporary data directory
        var testDir = Path.Combine(Path.GetTempPath(), $"phobri_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        var services = new ServiceCollection();
        var configManager = new ConfigurationManager(testDir);
        services.AddSingleton(configManager);

        var dataService = new DataService(Path.Combine(testDir, "data.db"));
        await dataService.InitializeAsync();
        services.AddSingleton<IDataService>(dataService);

        var pairingService = new PairingService(configManager);
        services.AddSingleton<IPairingService>(pairingService);

        services.AddSingleton<IWebSocketHandler, WebSocketHandler>();

        services.AddSingleton(sp => new SyncServer(
            port: _port,
            wsHandler: sp.GetRequiredService<IWebSocketHandler>(),
            pairingService: sp.GetRequiredService<IPairingService>(),
            dataService: sp.GetRequiredService<IDataService>()));

        _services = services.BuildServiceProvider();
        _server = _services.GetRequiredService<SyncServer>();
        _pairing = _services.GetRequiredService<IPairingService>();
        _pairingToken = _pairing.GeneratePairingToken();
        _pairing.ConfirmPairing(_pairingToken);
        _certFingerprint = _pairing.CertificateFingerprint;

        await _server.StartAsync();

        // Wait for the server to actually be listening
        await WaitForServerReady();
    }

    private async Task WaitForServerReady()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

        for (int i = 0; i < 20; i++)
        {
            try
            {
                var response = await client.GetAsync($"https://127.0.0.1:{_port}/api/v1/ping");
                if (response.IsSuccessStatusCode) return;
            }
            catch { /* Server not ready yet */ }
            await Task.Delay(100);
        }
        throw new Exception($"Server did not become ready on port {_port}");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _server.StopAsync(); } catch { }
        // ServiceProvider disposes all singletons including SyncServer
        (_services as IDisposable)?.Dispose();
        _cts.Dispose();
    }

    // ==================================================================
    // REST API Tests
    // ==================================================================

    [Fact]
    public async Task Ping_Returns_Ok()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.GetAsync($"https://127.0.0.1:{_port}/api/v1/ping");
        Assert.True(response.IsSuccessStatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    // ==================================================================
    // WebSocket Protocol Tests
    // ==================================================================

    [Fact]
    public async Task WebSocket_Connect_And_PingPong()
    {
        using var ws = await ConnectAsync();
        Assert.Equal(WebSocketState.Open, ws.State);

        // Send ping
        var ping = ProtocolMessage.Push("ping", new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await SendJsonAsync(ws, ping);

        // Receive pong
        var response = await ReceiveJsonAsync(ws);
        Assert.NotNull(response);
        Assert.Equal("pong", response.Action);
    }

    [Fact]
    public async Task WebSocket_PairInit_With_Valid_Token()
    {
        using var ws = await ConnectAsync();

        // Send pair.init with valid token
        var payload = JsonSerializer.SerializeToElement(new { token = _pairingToken });
        var pairInit = new ProtocolMessage
        {
            Type = MessageType.Request,
            Action = "pair.init",
            Payload = payload
        };
        await SendJsonAsync(ws, pairInit);

        // Expect pair.confirmed response
        var response = await ReceiveJsonAsync(ws);
        Assert.NotNull(response);
        Assert.Equal("pair.confirmed", response.Action);
    }

    [Fact]
    public async Task WebSocket_SmsNew_Push()
    {
        using var ws = await ConnectAsync();

        // Create a test SMS
        var sms = new SmsMessage
        {
            Id = 1001,
            ThreadId = 501,
            Address = "+15551234567",
            ContactName = "Test Contact",
            Body = "Hello from integration test!",
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = SmsType.Inbox,
            Read = true
        };

        // Send sms.new push
        var smsJson = JsonSerializer.SerializeToElement(sms, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "sms.new",
            Payload = smsJson
        };
        await SendJsonAsync(ws, push);

        // Give the server time to process
        await Task.Delay(100);

        // Verify the SMS was stored
        var dataService = _services.GetRequiredService<IDataService>();
        var messages = await dataService.GetSmsMessagesAsync(limit: 10);
        Assert.Contains(messages, m => m.Id == 1001 && m.Address == "+15551234567");
    }

    [Fact]
    public async Task WebSocket_SmsSync_Batch()
    {
        using var ws = await ConnectAsync();

        // Create a batch of SMS messages
        var messages = new List<SmsMessage>
        {
            new()
            {
                Id = 2001, ThreadId = 601, Address = "+15551111111",
                ContactName = "Alice", Body = "Message 1",
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = SmsType.Inbox, Read = true
            },
            new()
            {
                Id = 2002, ThreadId = 601, Address = "+15551111111",
                ContactName = "Alice", Body = "Message 2",
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = SmsType.Sent, Read = true
            },
            new()
            {
                Id = 2003, ThreadId = 602, Address = "+15552222222",
                ContactName = "Bob", Body = "Message 3",
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = SmsType.Inbox, Read = false
            }
        };

        var syncPayload = new SmsSyncPayload { Messages = messages, HasMore = false };
        var syncJson = JsonSerializer.SerializeToElement(syncPayload, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "sms.sync",
            Payload = syncJson
        };
        await SendJsonAsync(ws, push);

        // Give the server time to process the batch
        await Task.Delay(200);

        // Verify all messages were stored
        var dataService = _services.GetRequiredService<IDataService>();
        var stored = await dataService.GetSmsMessagesAsync(limit: 100);
        Assert.True(stored.Count >= 3);
        Assert.Contains(stored, m => m.Id == 2001);
        Assert.Contains(stored, m => m.Id == 2002);
        Assert.Contains(stored, m => m.Id == 2003);
    }

    [Fact]
    public async Task WebSocket_CallNew_Push()
    {
        using var ws = await ConnectAsync();

        var call = new CallLogEntry
        {
            Id = 3001,
            Number = "+15553333333",
            ContactName = "Charlie",
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Duration = 120,
            Type = CallType.Incoming
        };

        var callJson = JsonSerializer.SerializeToElement(call, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "call.new",
            Payload = callJson
        };
        await SendJsonAsync(ws, push);

        await Task.Delay(100);

        var dataService = _services.GetRequiredService<IDataService>();
        var calls = await dataService.GetCallLogAsync(limit: 10);
        Assert.Contains(calls, c => c.Id == 3001 && c.Number == "+15553333333");
    }

    [Fact]
    public async Task WebSocket_CallSync_Batch()
    {
        using var ws = await ConnectAsync();

        var calls = new List<CallLogEntry>
        {
            new()
            {
                Id = 4001, Number = "+15554444444", ContactName = "Diana",
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Duration = 300, Type = CallType.Outgoing
            },
            new()
            {
                Id = 4002, Number = "+15555555555", ContactName = "Eve",
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Duration = 0, Type = CallType.Missed
            }
        };

        var syncPayload = new CallSyncPayload { Calls = calls, HasMore = false };
        var syncJson = JsonSerializer.SerializeToElement(syncPayload, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "call.sync",
            Payload = syncJson
        };
        await SendJsonAsync(ws, push);

        await Task.Delay(200);

        var dataService = _services.GetRequiredService<IDataService>();
        var stored = await dataService.GetCallLogAsync(limit: 100);
        Assert.True(stored.Count >= 2);
        Assert.Contains(stored, c => c.Id == 4001);
        Assert.Contains(stored, c => c.Id == 4002);
    }

    [Fact]
    public async Task WebSocket_Request_SmsSync_From_Server()
    {
        using var ws = await ConnectAsync();

        // Server sends sms.sync.request
        var serverHandler = _services.GetRequiredService<IWebSocketHandler>();
        var request = ProtocolMessage.Request("sms.sync.request", new
        {
            after = (long?)null,
            limit = 50
        });
        await serverHandler.SendMessageAsync(request);

        // Client should receive the request
        var received = await ReceiveJsonAsync(ws);
        Assert.NotNull(received);
        Assert.Equal(MessageType.Request, received.Type);
        Assert.Equal("sms.sync.request", received.Action);
    }

    [Fact]
    public async Task WebSocket_MultiFrame_Message()
    {
        using var ws = await ConnectAsync();

        // Create a large message body that will likely span multiple frames
        var largeBody = new string('X', 5000) + "🍕"; // Large message spanning multiple frames

        var sms = new SmsMessage
        {
            Id = 5001,
            ThreadId = 701,
            Address = "+15556666666",
            ContactName = "Large Message Test",
            Body = largeBody,
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = SmsType.Inbox,
            Read = true
        };

        var smsJson = JsonSerializer.SerializeToElement(sms, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "sms.new",
            Payload = smsJson
        };
        await SendJsonAsync(ws, push);

        await Task.Delay(200);

        var dataService = _services.GetRequiredService<IDataService>();
        var messages = await dataService.GetSmsMessagesAsync(limit: 10);
        var stored = messages.FirstOrDefault(m => m.Id == 5001);
        Assert.NotNull(stored);
        Assert.Equal(largeBody, stored.Body);
    }

    [Fact]
    public async Task REST_Sms_Endpoint_Returns_Data()
    {
        // First push some data
        using var ws = await ConnectAsync();

        var sms = new SmsMessage
        {
            Id = 6001,
            ThreadId = 801,
            Address = "+15557777777",
            ContactName = "REST Test",
            Body = "Testing REST endpoint",
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = SmsType.Inbox,
            Read = true
        };
        var smsJson = JsonSerializer.SerializeToElement(sms, JsonContext.DefaultOptions);
        var push = new ProtocolMessage
        {
            Type = MessageType.Push,
            Action = "sms.new",
            Payload = smsJson
        };
        await SendJsonAsync(ws, push);
        await Task.Delay(100);

        // Now query via REST
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.GetAsync($"https://127.0.0.1:{_port}/api/v1/sms?limit=50");
        Assert.True(response.IsSuccessStatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var messagesArray = doc.RootElement.GetProperty("messages");
        Assert.True(messagesArray.GetArrayLength() > 0);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        // Set auth token header
        ws.Options.SetRequestHeader("X-Phobri-Token", _pairingToken);

        await ws.ConnectAsync(
            new Uri($"wss://127.0.0.1:{_port}/sync"),
            _cts.Token);

        return ws;
    }

    private static async Task SendJsonAsync(WebSocket ws, ProtocolMessage message)
    {
        var json = JsonSerializer.Serialize(message, JsonContext.DefaultOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static async Task<ProtocolMessage?> ReceiveJsonAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            // Handle multi-frame messages
            if (!result.EndOfMessage)
            {
                var full = new StringBuilder(json);
                while (!result.EndOfMessage)
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    full.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                json = full.ToString();
            }

            return ProtocolMessage.FromJson(json);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
