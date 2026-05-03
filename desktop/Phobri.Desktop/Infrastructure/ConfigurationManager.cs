using System.Text.Json;
using Phobri.Desktop.Models;

namespace Phobri.Desktop.Infrastructure;

/// <summary>
/// Manages app configuration stored on disk.
/// </summary>
public sealed class ConfigurationManager
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurationManager(string? configDir = null)
    {
        configDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".phobri");

        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "config.json");
        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>
    /// Load configuration or create default.
    /// </summary>
    public PhobriConfig Load()
    {
        if (!File.Exists(_configPath))
            return PhobriConfig.Default;

        var json = File.ReadAllText(_configPath);
        return JsonSerializer.Deserialize<PhobriConfig>(json, _jsonOptions) ?? PhobriConfig.Default;
    }

    /// <summary>
    /// Save configuration to disk.
    /// </summary>
    public void Save(PhobriConfig config)
    {
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        File.WriteAllText(_configPath, json);
    }

    /// <summary>
    /// Get the phobri data directory.
    /// </summary>
    public string DataDir => Path.GetDirectoryName(_configPath)!;
}

/// <summary>
/// Application configuration model.
/// </summary>
public sealed record PhobriConfig
{
    /// <summary>Current pairing token (null if not paired).
    /// Deprecated: use EncryptedPairingToken for new setups.</summary>
    public string? PairingToken { get; init; }

    /// <summary>Pairing token encrypted with DEK (base64).</summary>
    public string? EncryptedPairingToken { get; init; }

    /// <summary>SHA-256 fingerprint of the TLS certificate.</summary>
    public string? CertFingerprint { get; init; }

    /// <summary>Last known phone FCM token (optional).</summary>
    public string? FcmToken { get; init; }

    /// <summary>Path to the Firebase service account JSON key file.
    /// Required for FCM push-to-wake functionality.</summary>
    public string? FcmServiceAccountPath { get; init; }

    /// <summary>Last known external IP address.</summary>
    public string? ExternalIp { get; init; }

    /// <summary>PBKDF2 salt for password-based KEK derivation (base64).</summary>
    public string? Salt { get; init; }

    /// <summary>Data Encryption Key encrypted with KEK (base64).</summary>
    public string? EncryptedDek { get; init; }

    /// <summary>Server Identity Key encrypted with KEK (base64).</summary>
    public string? EncryptedSik { get; init; }

    /// <summary>Auto-lock timeout in minutes (0 = never auto-lock).</summary>
    public int AutoLockMinutes { get; init; } = 2;

    /// <summary>WebSocket server port.</summary>
    public int WsPort { get; init; } = 8765;

    /// <summary>UDP wake port on the phone.</summary>
    public int UdpWakePort { get; init; } = 9876;

    public static PhobriConfig Default => new();
}
