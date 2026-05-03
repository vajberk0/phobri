using System.Security.Cryptography;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Services;

/// <summary>
/// Manages password-based encryption for the Phobri desktop app.
/// Handles key derivation, envelope encryption of DEK/SIK,
/// and provides the decrypted keys to other services.
/// </summary>
public interface IPasswordManagerService
{
    /// <summary>Whether a password has been configured (first-time setup done).</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the vault is currently unlocked (password entered).</summary>
    bool IsUnlocked { get; }

    /// <summary>The Data Encryption Key (only available when unlocked).</summary>
    byte[]? DataEncryptionKey { get; }

    /// <summary>The Server Identity Key for challenge-response (only available when unlocked).</summary>
    byte[]? ServerIdentityKey { get; }

    /// <summary>The decrypted pairing token (only available when unlocked).</summary>
    string? PairingToken { get; }

    /// <summary>Auto-lock timeout in minutes (0 = never).</summary>
    int AutoLockMinutes { get; }

    /// <summary>
    /// Set up the password for the first time.
    /// Generates salt, DEK, SIK and encrypts them with the password-derived KEK.
    /// </summary>
    void SetupPassword(string password);

    /// <summary>
    /// Unlock the vault with the password.
    /// Derives KEK and decrypts DEK, SIK, and pairing token.
    /// </summary>
    bool Unlock(string password);

    /// <summary>
    /// Lock the vault, clearing all decrypted keys from memory.
    /// </summary>
    void Lock();

    /// <summary>
    /// Change the password.
    /// Re-encrypts the DEK and SIK with the new password-derived KEK.
    /// </summary>
    bool ChangePassword(string oldPassword, string newPassword);

    /// <summary>
    /// Store the pairing token (encrypted with DEK).
    /// </summary>
    void StorePairingToken(string token);

    /// <summary>
    /// Called when activity is detected to reset the auto-lock timer.
    /// </summary>
    void NotifyActivity();

    /// <summary>
    /// Check if auto-lock has expired and lock if so.
    /// Returns true if still unlocked, false if locked.
    /// </summary>
    bool CheckAutoLock();

    /// <summary>
    /// Set the auto-lock timeout (persisted to config).
    /// </summary>
    void SetAutoLockMinutes(int minutes);

    /// <summary>
    /// Raised when the vault is locked (auto-lock or manual).
    /// Subscribers can close the database and other protected resources.
    /// </summary>
    event EventHandler? VaultLocked;
}

public sealed class PasswordManagerService : IPasswordManagerService, IDisposable
{
    private readonly ConfigurationManager _config;
    private Timer? _autoLockTimer;
    private DateTime _lastActivity = DateTime.UtcNow;

    // Decrypted keys (null when locked)
    private byte[]? _dek;
    private byte[]? _sik;
    private string? _pairingToken;
    private bool _isUnlocked;

    /// <inheritdoc/>
    public event EventHandler? VaultLocked;

    public PasswordManagerService(ConfigurationManager config)
    {
        _config = config;
        var savedConfig = config.Load();

        // Load auto-lock setting
        AutoLockMinutes = savedConfig.AutoLockMinutes;

        // Start auto-lock timer even before password setup
        _autoLockTimer = new Timer(OnAutoLockTimer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public bool IsConfigured
    {
        get
        {
            var config = _config.Load();
            return config.Salt is not null
                && config.EncryptedDek is not null
                && config.EncryptedSik is not null;
        }
    }

    /// <inheritdoc/>
    public bool IsUnlocked => _isUnlocked;

    /// <inheritdoc/>
    public byte[]? DataEncryptionKey => _isUnlocked ? _dek : null;

    /// <inheritdoc/>
    public byte[]? ServerIdentityKey => _isUnlocked ? _sik : null;

    /// <inheritdoc/>
    public string? PairingToken => _isUnlocked ? _pairingToken : null;

    /// <inheritdoc/>
    public int AutoLockMinutes { get; private set; }

    /// <inheritdoc/>
    public void SetupPassword(string password)
    {
        if (IsConfigured)
            throw new InvalidOperationException("Password is already configured. Use ChangePassword instead.");

        // Generate salt and keys
        var salt = CryptoService.GenerateSalt();
        var dek = CryptoService.GenerateKey();
        var sik = CryptoService.GenerateKey();

        // Derive KEK from password
        var kek = CryptoService.DeriveKey(password, salt);

        // Encrypt DEK and SIK with KEK
        var encryptedDek = CryptoService.EncryptBytes(dek, kek);
        var encryptedSik = CryptoService.EncryptBytes(sik, kek);

        // Save to config
        var config = _config.Load() with
        {
            Salt = Convert.ToBase64String(salt),
            EncryptedDek = Convert.ToBase64String(encryptedDek),
            EncryptedSik = Convert.ToBase64String(encryptedSik)
        };
        _config.Save(config);

        // Set in-memory keys
        _dek = dek;
        _sik = sik;
        _isUnlocked = true;
        _lastActivity = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public bool Unlock(string password)
    {
        var config = _config.Load();
        if (!IsConfigured)
            return false;

        try
        {
            var salt = Convert.FromBase64String(config.Salt!);
            var kek = CryptoService.DeriveKey(password, salt);

            // Decrypt DEK
            var encryptedDek = Convert.FromBase64String(config.EncryptedDek!);
            _dek = CryptoService.DecryptBytes(encryptedDek, kek);

            // Decrypt SIK
            var encryptedSik = Convert.FromBase64String(config.EncryptedSik!);
            _sik = CryptoService.DecryptBytes(encryptedSik, kek);

            // Decrypt pairing token (if set)
            if (config.EncryptedPairingToken is not null)
            {
                try
                {
                    _pairingToken = CryptoService.DecryptString(config.EncryptedPairingToken, _dek);
                }
                catch
                {
                    // Token might be from before encryption was added
                    _pairingToken = config.PairingToken;
                }
            }
            else
            {
                _pairingToken = config.PairingToken;
            }

            _isUnlocked = true;
            _lastActivity = DateTime.UtcNow;
            return true;
        }
        catch (CryptographicException)
        {
            // Wrong password
            _dek = null;
            _sik = null;
            _pairingToken = null;
            _isUnlocked = false;
            return false;
        }
        catch (FormatException)
        {
            // Corrupted data
            _dek = null;
            _sik = null;
            _pairingToken = null;
            _isUnlocked = false;
            return false;
        }
    }

    /// <inheritdoc/>
    public void Lock()
    {
        _dek = null;
        _sik = null;
        _pairingToken = null;
        _isUnlocked = false;
        VaultLocked?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public bool ChangePassword(string oldPassword, string newPassword)
    {
        if (!IsConfigured)
            return false;

        // Unlock with old password first
        if (!Unlock(oldPassword))
            return false;

        try
        {
            var config = _config.Load();
            var newSalt = CryptoService.GenerateSalt();
            var newKek = CryptoService.DeriveKey(newPassword, newSalt);

            // Re-encrypt DEK and SIK with new KEK
            var encryptedDek = CryptoService.EncryptBytes(_dek!, newKek);
            var encryptedSik = CryptoService.EncryptBytes(_sik!, newKek);

            // Save to config
            config = config with
            {
                Salt = Convert.ToBase64String(newSalt),
                EncryptedDek = Convert.ToBase64String(encryptedDek),
                EncryptedSik = Convert.ToBase64String(encryptedSik)
            };
            _config.Save(config);

            // Note: _dek and _sik are still valid (unchanged)
            // Pairing token is still valid
            _lastActivity = DateTime.UtcNow;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void StorePairingToken(string token)
    {
        if (!_isUnlocked || _dek is null)
            throw new InvalidOperationException("Cannot store pairing token while locked.");

        _pairingToken = token;

        var config = _config.Load();
        var encryptedToken = CryptoService.EncryptString(token, _dek);
        config = config with
        {
            EncryptedPairingToken = encryptedToken,
            PairingToken = null // Remove plaintext version
        };
        _config.Save(config);
    }

    /// <inheritdoc/>
    public void NotifyActivity()
    {
        if (_isUnlocked)
            _lastActivity = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public bool CheckAutoLock()
    {
        if (!_isUnlocked || AutoLockMinutes <= 0)
            return _isUnlocked;

        if ((DateTime.UtcNow - _lastActivity).TotalMinutes >= AutoLockMinutes)
        {
            Lock();
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public void SetAutoLockMinutes(int minutes)
    {
        AutoLockMinutes = minutes;

        var config = _config.Load() with { AutoLockMinutes = minutes };
        _config.Save(config);
    }

    private void OnAutoLockTimer(object? state)
    {
        CheckAutoLock();
    }

    public void Dispose()
    {
        _autoLockTimer?.Dispose();
        _autoLockTimer = null;
        Lock();
    }
}
