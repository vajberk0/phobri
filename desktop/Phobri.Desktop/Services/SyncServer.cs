using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Phobri.Desktop.Services;

/// <summary>
/// Embedded WebSocket + HTTP server using ASP.NET Core Kestrel.
/// Runs alongside the Avalonia UI.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private readonly WebApplication _app;
    private Task? _runningTask;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public bool IsRunning { get; private set; }

    public SyncServer(
        int port,
        IWebSocketHandler wsHandler,
        IPairingService pairingService,
        IDataService dataService)
    {
        Port = port;

        var builder = WebApplication.CreateBuilder();

        // Disable default logging to avoid console spam
        builder.Logging.ClearProviders();

        // Register services
        builder.Services.AddSingleton(wsHandler);
        builder.Services.AddSingleton(pairingService);
        builder.Services.AddSingleton(dataService);

        // Configure Kestrel to listen on all interfaces
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                if (pairingService.Certificate is not null)
                {
                    listenOptions.UseHttps(pairingService.Certificate);
                }
            });

            // HTTP listener on loopback for health checks (port + 1)
            options.Listen(IPAddress.Loopback, port + 1);
        });

        _app = builder.Build();

        // Enable WebSocket support
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // Map WebSocket endpoint
        _app.Map("/sync", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            // Validate pairing token from header
            var token = context.Request.Headers["X-Phobri-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(token) || !pairingService.ValidateToken(token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized: invalid or missing token");
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await wsHandler.HandleConnectionAsync(webSocket, context.RequestAborted);
        });

        // REST API endpoints
        _app.MapGet("/api/v1/ping", () =>
        {
            return Results.Ok(new
            {
                status = "ok",
                version = "1.0.0",
                paired = pairingService.IsPaired,
                fingerprint = pairingService.CertificateFingerprint,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        });

        _app.MapPost("/api/v1/pair/request", () =>
        {
            var token = pairingService.GeneratePairingToken();
            return Results.Ok(new
            {
                token,
                fingerprint = pairingService.CertificateFingerprint
            });
        });

        _app.MapPost("/api/v1/pair/confirm", (PairConfirmRequest request) =>
        {
            if (pairingService.ConfirmPairing(request.Token))
            {
                return Results.Ok(new { status = "paired" });
            }
            return Results.BadRequest(new { error = "Invalid token" });
        });

        _app.MapGet("/api/v1/sms", async (long? after, int? limit) =>
        {
            var messages = await dataService.GetSmsMessagesAsync(
                after, limit ?? 100);
            return Results.Ok(new { messages, hasMore = messages.Count >= (limit ?? 100) });
        });

        _app.MapGet("/api/v1/sms/conversation/{address}", async (string address, int? limit) =>
        {
            var messages = await dataService.GetConversationAsync(
                address, limit ?? 100);
            return Results.Ok(new { messages });
        });

        _app.MapGet("/api/v1/calls", async (long? after, int? limit) =>
        {
            var calls = await dataService.GetCallLogAsync(
                after, limit ?? 100);
            return Results.Ok(new { calls, hasMore = calls.Count >= (limit ?? 100) });
        });
    }

    public async Task StartAsync()
    {
        _runningTask = _app.RunAsync(_cts.Token);
        IsRunning = true;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_runningTask is not null)
        {
            try
            {
                await _runningTask;
            }
            catch (OperationCanceledException) { }
        }
        IsRunning = false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _app.DisposeAsync().AsTask().Wait();
        _cts.Dispose();
    }
}

public sealed record PairConfirmRequest
{
    public string Token { get; init; } = string.Empty;
}
