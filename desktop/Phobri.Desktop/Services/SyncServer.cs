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
/// Requires password unlock before accepting data connections.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private readonly WebApplication _app;
    private Task? _runningTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly IPasswordManagerService _passwordManager;
    private readonly IPairingService _pairingService;
    private readonly IDataService _dataService;
    private readonly ILogService _log;

    public int Port { get; }
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Fired when a request is rejected because the server is locked (HTTP 423).
    /// The UI can listen for this to auto-prompt for the unlock password.
    /// </summary>
    public event EventHandler? LockedRequestReceived;

    public SyncServer(
        int port,
        IWebSocketHandler wsHandler,
        IPairingService pairingService,
        IDataService dataService,
        IPasswordManagerService passwordManager,
        ILogService logService)
    {
        Port = port;
        _passwordManager = passwordManager;
        _pairingService = pairingService;
        _dataService = dataService;
        _log = logService;

        // Close the encrypted database when the vault auto-locks
        passwordManager.VaultLocked += async (_, _) =>
        {
            try { await dataService.LockAsync(); }
            catch { /* Best-effort on shutdown */ }
        };

        var builder = WebApplication.CreateBuilder();

        // Disable default logging to avoid console spam
        builder.Logging.ClearProviders();

        // Register services
        builder.Services.AddSingleton(wsHandler);
        builder.Services.AddSingleton(pairingService);
        builder.Services.AddSingleton(dataService);
        builder.Services.AddSingleton(passwordManager);

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

        // Simple request logging middleware
        _app.Use(async (context, next) =>
        {
            var msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} → {context.Request.Method} {context.Request.Path}";
            _log.Log("REST", msg);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            await next();
        });

        // Map WebSocket endpoint
        _app.Map("/sync", async (HttpContext context) =>
        {
            _log.Log("REST", $"WS upgrade request from {context.Connection.RemoteIpAddress}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WS connection attempt from {context.Connection.RemoteIpAddress}");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            // Check if server is unlocked
            if (!passwordManager.IsUnlocked)
            {
                _log.Log("REST", "WS rejected — server locked (423)");
                context.Response.StatusCode = 423; // Locked
                LockedRequestReceived?.Invoke(this, EventArgs.Empty);
                await context.Response.WriteAsync("Server is locked. Unlock with password first.");
                return;
            }

            // Authentication is done via the first WebSocket message (pair.init),
            // not via HTTP header. The WebSocketHandler validates the token.
            // (REST endpoints still use X-Phobri-Token header.)

            // Notify password manager of activity (reset auto-lock timer)
            passwordManager.NotifyActivity();

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
                locked = !passwordManager.IsUnlocked,
                paired = pairingService.IsPaired,
                fingerprint = pairingService.CertificateFingerprint,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        });

        _app.MapGet("/api/v1/auth/status", () =>
        {
            return Results.Ok(new
            {
                locked = !passwordManager.IsUnlocked,
                configured = passwordManager.IsConfigured,
                autoLockMinutes = passwordManager.AutoLockMinutes
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

        _app.MapGet("/api/v1/sms", async (HttpContext context, long? after, int? limit) =>
        {
            var tokenError = RequireToken(context);
            if (tokenError is not null) return tokenError;

            var messages = await dataService.GetSmsMessagesAsync(
                after, Math.Clamp(limit ?? 100, 1, 500));
            return Results.Ok(new { messages, hasMore = messages.Count >= (limit ?? 100) });
        });

        _app.MapGet("/api/v1/sms/conversation/{address}", async (HttpContext context, string address, int? limit) =>
        {
            var tokenError = RequireToken(context);
            if (tokenError is not null) return tokenError;

            var messages = await dataService.GetConversationAsync(
                address, Math.Clamp(limit ?? 100, 1, 500));
            return Results.Ok(new { messages });
        });

        _app.MapGet("/api/v1/calls", async (HttpContext context, long? after, int? limit) =>
        {
            var tokenError = RequireToken(context);
            if (tokenError is not null) return tokenError;

            var calls = await dataService.GetCallLogAsync(
                after, Math.Clamp(limit ?? 100, 1, 500));
            return Results.Ok(new { calls, hasMore = calls.Count >= (limit ?? 100) });
        });
    }

    /// <summary>
    /// Validates the X-Phobri-Token header against the stored pairing token.
    /// Returns an error response (401/423) on failure, or null if the request is authorized.
    /// </summary>
    private IResult? RequireToken(HttpContext context)
    {
        if (!_passwordManager.IsUnlocked)
        {
            LockedRequestReceived?.Invoke(this, EventArgs.Empty);
            return Results.Json(new { error = "Server is locked" }, statusCode: 423);
        }

        if (!_pairingService.IsPaired)
            return Results.Json(new { error = "No device paired" }, statusCode: 401);

        var token = context.Request.Headers["X-Phobri-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
            return Results.Json(new { error = "Missing X-Phobri-Token header" }, statusCode: 401);

        if (!_pairingService.ValidateToken(token))
            return Results.Json(new { error = "Invalid token" }, statusCode: 401);

        _passwordManager.NotifyActivity();
        return null;
    }

    public async Task StartAsync()
    {
        _log.Log("SERVER", $"Starting on port {Port} (HTTPS) and {Port + 1} (HTTP health)");
        _runningTask = _app.RunAsync(_cts.Token);
        IsRunning = true;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _log.Log("SERVER", "Stopping server");
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
