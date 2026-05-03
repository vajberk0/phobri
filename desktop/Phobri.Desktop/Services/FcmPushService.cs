using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Services;

/// <summary>
/// Sends FCM push messages to the Android device to wake it up
/// and tell it the current server address.
/// Uses Firebase Admin SDK with a service account JSON key file.
/// </summary>
public interface IFcmPushService
{
    /// <summary>Whether the service has been initialized with valid credentials.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize (or re-initialize) the Firebase app with a service account key file.
    /// Call on startup and whenever the key path changes.
    /// </summary>
    /// <param name="serviceAccountPath">Path to the Firebase service account JSON key file.</param>
    /// <returns>True if initialization succeeded.</returns>
    bool Initialize(string serviceAccountPath);

    /// <summary>
    /// Send a wake push message to the phone, telling it to connect to the given host/port.
    /// </summary>
    /// <param name="fcmToken">The phone's FCM registration token.</param>
    /// <param name="serverHost">Current server hostname or IP.</param>
    /// <param name="serverPort">Server WSS port (default 8765).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was accepted by FCM.</returns>
    Task<bool> SendWakeAsync(string fcmToken, string serverHost, int serverPort = 8765, CancellationToken ct = default);

    /// <summary>Store the phone's FCM registration token persistently.</summary>
    Task StoreFcmTokenAsync(string token);

    /// <summary>Get the stored FCM token (or null).</summary>
    string? GetStoredFcmToken();
}

public sealed class FcmPushService : IFcmPushService, IDisposable
{
    private readonly ConfigurationManager _config;
    private readonly ILogService _log;
    private FirebaseApp? _fcmApp;

    public FcmPushService(ConfigurationManager config, ILogService logService)
    {
        _config = config;
        _log = logService;
    }

    /// <inheritdoc/>
    public bool IsInitialized => _fcmApp is not null;

    /// <inheritdoc/>
    public bool Initialize(string serviceAccountPath)
    {
        // Clean up previous instance
        if (_fcmApp is not null)
        {
            try { _fcmApp.Delete(); } catch { }
            _fcmApp = null;
        }

        if (string.IsNullOrWhiteSpace(serviceAccountPath))
        {
            _log.Log("FCM", "No service account path configured — FCM disabled");
            return false;
        }

        if (!File.Exists(serviceAccountPath))
        {
            _log.Log("FCM", $"Service account file not found: {serviceAccountPath}");
            return false;
        }

        try
        {
            var credential = GoogleCredential.FromFile(serviceAccountPath);
            _fcmApp = FirebaseApp.Create(new AppOptions
            {
                Credential = credential
            });
            _log.Log("FCM", "Initialized with service account");
            return true;
        }
        catch (Exception ex)
        {
            _log.Log("FCM", $"Failed to initialize: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendWakeAsync(
        string fcmToken, string serverHost, int serverPort = 8765,
        CancellationToken ct = default)
    {
        if (_fcmApp is null)
        {
            _log.Log("FCM", "Cannot send wake — not initialized");
            return false;
        }

        if (string.IsNullOrWhiteSpace(fcmToken))
        {
            _log.Log("FCM", "Cannot send wake — no FCM token for phone");
            return false;
        }

        try
        {
            var message = new Message
            {
                Token = fcmToken,
                Data = new Dictionary<string, string>
                {
                    ["type"] = "wake",
                    ["serverHost"] = serverHost,
                    ["serverPort"] = serverPort.ToString()
                },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    // Time to live: 0 means deliver now or never (no stale wakes
                    // arriving hours later after a connection is already established)
                    TimeToLive = TimeSpan.FromSeconds(0)
                }
            };

            var response = await FirebaseMessaging.DefaultInstance
                .SendAsync(message, ct);

            _log.Log("FCM", $"Wake sent to phone — server={serverHost}:{serverPort}, response={response}");
            return true;
        }
        catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            // The phone's FCM token is no longer valid (app uninstalled, etc.)
            _log.Log("FCM", $"FCM token unregistered — clearing stored token");
            await ClearStoredTokenAsync();
            return false;
        }
        catch (Exception ex)
        {
            _log.Log("FCM", $"Failed to send wake: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Store the phone's FCM token in the config.
    /// Called by WebSocketHandler when it receives a fcm.token push.
    /// </summary>
    public async Task StoreFcmTokenAsync(string token)
    {
        var config = _config.Load();
        if (config.FcmToken == token) return;

        config = config with { FcmToken = token };
        await Task.Run(() => _config.Save(config));
        _log.Log("FCM", $"Stored phone FCM token: {token[..Math.Min(8, token.Length)]}...");
    }

    /// <summary>
    /// Get the stored FCM token (or null if none).
    /// </summary>
    public string? GetStoredFcmToken()
    {
        return _config.Load().FcmToken;
    }

    private async Task ClearStoredTokenAsync()
    {
        var config = _config.Load();
        config = config with { FcmToken = null };
        await Task.Run(() => _config.Save(config));
    }

    public void Dispose()
    {
        if (_fcmApp is not null)
        {
            try { _fcmApp.Delete(); } catch { }
            _fcmApp = null;
        }
    }
}
