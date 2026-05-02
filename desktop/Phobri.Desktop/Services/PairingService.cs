using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Services;

/// <summary>
/// Manages the Trust-on-First-Use pairing between desktop and Android.
/// Handles pairing token generation, certificate management, and pairing state.
/// </summary>
public interface IPairingService
{
    /// <summary>Whether a device is currently paired.</summary>
    bool IsPaired { get; }

    /// <summary>The current pairing token (null if not paired).</summary>
    string? PairingToken { get; }

    /// <summary>The TLS certificate used for secure communication.</summary>
    X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 fingerprint of the TLS certificate.</summary>
    string CertificateFingerprint { get; }

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
    private readonly string _certPath;
    private X509Certificate2 _certificate;

    public PairingService(ConfigurationManager config)
    {
        _config = config;
        _certPath = Path.Combine(config.DataDir, "server.pfx");
        _certificate = LoadOrCreateCertificate();

        var savedConfig = config.Load();
        PairingToken = savedConfig.PairingToken;
    }

    /// <inheritdoc/>
    public bool IsPaired => PairingToken is not null;

    /// <inheritdoc/>
    public string? PairingToken { get; private set; }

    /// <inheritdoc/>
    public X509Certificate2 Certificate => _certificate;

    /// <inheritdoc/>
    public string CertificateFingerprint =>
        TlsCertificateGenerator.GetFingerprint(_certificate);

    /// <inheritdoc/>
    public string GeneratePairingToken()
    {
        // Generate a 32-byte random token as hex string
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexStringLower(bytes);

        // Don't save yet — wait for confirmation
        return token;
    }

    /// <inheritdoc/>
    public bool ConfirmPairing(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
            return false;

        PairingToken = token;

        var config = _config.Load();
        config = config with
        {
            PairingToken = token,
            CertFingerprint = CertificateFingerprint
        };
        _config.Save(config);

        return true;
    }

    /// <inheritdoc/>
    public void Unpair()
    {
        PairingToken = null;

        var config = _config.Load();
        config = config with
        {
            PairingToken = null,
            FcmToken = null
        };
        _config.Save(config);
    }

    /// <summary>
    /// Check if a provided token matches our stored token.
    /// </summary>
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
