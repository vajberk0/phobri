using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Services;

/// <summary>
/// Manages the Trust-on-First-Use pairing between desktop and Android.
/// Handles pairing token generation, certificate management, and pairing state.
/// Integrates with PasswordManagerService for encrypted token storage.
/// </summary>
public interface IPairingService
{
    /// <summary>Whether a device is currently paired.</summary>
    bool IsPaired { get; }

    /// <summary>The current pairing token (null if not paired or locked).</summary>
    string? PairingToken { get; }

    /// <summary>The TLS certificate used for secure communication.</summary>
    X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 fingerprint of the TLS certificate.</summary>
    string CertificateFingerprint { get; }

    /// <summary>The Server Identity Key for challenge-response (null if locked).</summary>
    byte[]? ServerIdentityKey { get; }

    /// <summary>Generate a new pairing token for a pending pair request.</summary>
    string GeneratePairingToken();

    /// <summary>Validate and confirm a pairing token from the Android device.</summary>
    bool ConfirmPairing(string token);

    /// <summary>Clear the current pairing (factory reset).</summary>
    void Unpair();

    /// <summary>Check if a provided token matches the stored pairing token.</summary>
    bool ValidateToken(string token);
}

public sealed class PairingService : IPairingService
{
    private readonly ConfigurationManager _config;
    private readonly IPasswordManagerService _passwordManager;
    private readonly string _certPath;
    private X509Certificate2 _certificate;

    public PairingService(ConfigurationManager config, IPasswordManagerService passwordManager)
    {
        _config = config;
        _passwordManager = passwordManager;
        _certPath = Path.Combine(config.DataDir, "server.pfx");
        _certificate = LoadOrCreateCertificate();
    }

    /// <inheritdoc/>
    public bool IsPaired =>
        _passwordManager.IsUnlocked && _passwordManager.PairingToken is not null;

    /// <inheritdoc/>
    public string? PairingToken => _passwordManager.PairingToken;

    /// <inheritdoc/>
    public X509Certificate2 Certificate => _certificate;

    /// <inheritdoc/>
    public string CertificateFingerprint =>
        TlsCertificateGenerator.GetFingerprint(_certificate);

    /// <inheritdoc/>
    public byte[]? ServerIdentityKey => _passwordManager.ServerIdentityKey;

    /// <inheritdoc/>
    public string GeneratePairingToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    /// <inheritdoc/>
    public bool ConfirmPairing(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
            return false;

        if (!_passwordManager.IsUnlocked)
            return false;

        // Store token encrypted with DEK
        _passwordManager.StorePairingToken(token);

        // Update cert fingerprint in config
        var config = _config.Load();
        config = config with { CertFingerprint = CertificateFingerprint };
        _config.Save(config);

        return true;
    }

    /// <inheritdoc/>
    public void Unpair()
    {
        // Lock the password manager to clear in-memory token, then unlock
        _passwordManager.Lock();

        var config = _config.Load();
        config = config with
        {
            PairingToken = null,
            EncryptedPairingToken = null,
            FcmToken = null
        };
        _config.Save(config);
    }

    /// <inheritdoc/>
    public bool ValidateToken(string token)
    {
        if (!IsPaired || PairingToken is null)
            return false;

        return string.Equals(PairingToken, token, StringComparison.Ordinal);
    }

    private X509Certificate2 LoadOrCreateCertificate()
    {
        if (File.Exists(_certPath))
        {
            try
            {
                return TlsCertificateGenerator.LoadFromFile(_certPath);
            }
            catch
            {
                // Certificate is corrupted, regenerate
            }
        }

        var cert = TlsCertificateGenerator.GenerateSelfSigned();
        TlsCertificateGenerator.SaveToFile(cert, _certPath);
        return cert;
    }
}
