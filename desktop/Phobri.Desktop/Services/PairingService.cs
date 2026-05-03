using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Phobri.Desktop.Infrastructure;
using QRCoder;

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

    /// <summary>Whether a pending (unconfirmed) pairing token exists.</summary>
    bool HasPendingToken { get; }

    /// <summary>Generate a new pairing token for a pending pair request.</summary>
    string GeneratePairingToken();

    /// <summary>Validate and confirm a pairing token from the Android device.</summary>
    bool ConfirmPairing(string token);

    /// <summary>Clear the current pairing (factory reset).</summary>
    void Unpair();

    /// <summary>Check if a provided token matches the stored or pending pairing token.</summary>
    bool ValidateToken(string token);

    /// <summary>Get local network IP addresses (non-loopback IPv4 and IPv6).</summary>
    IReadOnlyList<string> GetLocalAddresses();

    /// <summary>Generate QR code PNG bytes for the pairing info using the given host and port.</summary>
    byte[]? GenerateQrCode(string host, int port);
}

public sealed class PairingService : IPairingService
{
    private readonly ConfigurationManager _config;
    private readonly IPasswordManagerService _passwordManager;
    private readonly string _certPath;
    private X509Certificate2 _certificate;

    /// <summary>Pending pairing token that hasn't been confirmed yet.</summary>
    private string? _pendingToken;

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
    public bool HasPendingToken =>
        _passwordManager.IsUnlocked && _pendingToken is not null && !IsPaired;

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
        // Reuse existing pending token if there is one
        if (_pendingToken is not null)
            return _pendingToken;

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexStringLower(bytes);
        _pendingToken = token;
        return token;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetLocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                // Skip loopback and tunnel interfaces
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        addresses.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { /* Best-effort */ }

        // Sort: private ranges first, then others
        addresses.Sort((a, b) =>
        {
            bool aPrivate = IsPrivateIp(a);
            bool bPrivate = IsPrivateIp(b);
            if (aPrivate && !bPrivate) return -1;
            if (!aPrivate && bPrivate) return 1;
            return string.CompareOrdinal(a, b);
        });

        return addresses;
    }

    /// <inheritdoc/>
    public byte[]? GenerateQrCode(string host, int port)
    {
        if (_pendingToken is null)
            return null;

        try
        {
            // Format: phobri://pair?h=<host>&p=<port>&t=<token>&f=<fingerprint>
            var fingerprint = CertificateFingerprint;
            var uri = $"phobri://pair?h={Uri.EscapeDataString(host)}&p={port}&t={_pendingToken}&f={fingerprint}";
            return PngByteQRCodeHelper.GetQRCode(uri, QRCodeGenerator.ECCLevel.M, 10);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrivateIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return false;

        byte[] bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        return false;
    }

    /// <inheritdoc/>
    public bool ConfirmPairing(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
        {
            Console.WriteLine($"[ConfirmPairing] Invalid token format (len={token?.Length ?? 0})");
            return false;
        }

        if (!_passwordManager.IsUnlocked)
        {
            Console.WriteLine("[ConfirmPairing] Password manager is LOCKED");
            return false;
        }

        // Accept the pending token or an already-stored token
        if (_pendingToken is not null &&
            string.Equals(_pendingToken, token, StringComparison.Ordinal))
        {
            Console.WriteLine("[ConfirmPairing] Pending token matches — storing...");
            // Pending token confirmed — store it
            _passwordManager.StorePairingToken(token);
            _pendingToken = null;

            var config = _config.Load();
            config = config with { CertFingerprint = CertificateFingerprint };
            _config.Save(config);

            Console.WriteLine("[ConfirmPairing] Pairing confirmed and saved!");
            return true;
        }

        Console.WriteLine($"[ConfirmPairing] Pending token mismatch. Pending: {(_pendingToken is null ? "null" : _pendingToken[..16] + "...")}, Received: {token[..16]}...");

        // If already paired (e.g., re-confirming after restart), validate against stored token
        if (IsPaired && PairingToken is not null &&
            string.Equals(PairingToken, token, StringComparison.Ordinal))
        {
            Console.WriteLine("[ConfirmPairing] Already paired — token matches stored");
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Unpair()
    {
        _pendingToken = null;

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
        // Check against confirmed pairing token
        if (IsPaired && PairingToken is not null)
        {
            var match = string.Equals(PairingToken, token, StringComparison.Ordinal);
            Console.WriteLine($"[ValidateToken] Checking stored token: match={match}");
            return match;
        }

        // Check against pending (unconfirmed) token
        if (_pendingToken is not null &&
            string.Equals(_pendingToken, token, StringComparison.Ordinal))
        {
            Console.WriteLine("[ValidateToken] Pending token MATCH");
            return true;
        }

        Console.WriteLine($"[ValidateToken] No match. Pending token is {(_pendingToken is null ? "null" : "set")}");
        return false;
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
