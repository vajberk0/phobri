using Xunit;
using Phobri.Desktop.Services;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Tests.Services;

/// <summary>
/// Tests for PairingService with encrypted token storage via PasswordManagerService.
/// </summary>
public sealed class PairingServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigurationManager _config;
    private readonly IPasswordManagerService _passwordManager;
    private readonly IPairingService _service;

    public PairingServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "phobri-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);

        _config = new ConfigurationManager(_testDir);
        _passwordManager = new PasswordManagerService(_config);

        // Set up password so we have a DEK and can store pairing token encrypted
        _passwordManager.SetupPassword("test-password-123");
        Assert.True(_passwordManager.IsUnlocked);

        _service = new PairingService(_config, _passwordManager);
    }

    [Fact]
    public void NewService_IsNotPaired_WhenNoToken()
    {
        Assert.False(_service.IsPaired);
        Assert.Null(_service.PairingToken);
    }

    [Fact]
    public void GeneratePairingToken_Returns64CharHex()
    {
        var token = _service.GeneratePairingToken();

        Assert.NotNull(token);
        Assert.Equal(64, token.Length);
        // All hex characters
        Assert.All(token, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void GeneratePairingToken_DoesNotSetPaired()
    {
        _service.GeneratePairingToken();

        // Generating a token does not pair - confirmation is separate
        Assert.False(_service.IsPaired);
    }

    [Fact]
    public void ConfirmPairing_SetsPairedState()
    {
        var token = _service.GeneratePairingToken();
        var result = _service.ConfirmPairing(token);

        Assert.True(result);
        Assert.True(_service.IsPaired);
        Assert.Equal(token, _service.PairingToken);
    }

    [Fact]
    public void ConfirmPairing_RejectsInvalidToken()
    {
        var result = _service.ConfirmPairing("not-valid-hex-64");

        Assert.False(result);
        Assert.False(_service.IsPaired);
    }

    [Fact]
    public void ValidateToken_ReturnsTrue_ForCorrectToken()
    {
        var token = _service.GeneratePairingToken();
        _service.ConfirmPairing(token);

        Assert.True(_service.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_ReturnsFalse_ForWrongToken()
    {
        var token = _service.GeneratePairingToken();
        _service.ConfirmPairing(token);

        Assert.False(_service.ValidateToken("wrong-token"));
    }

    [Fact]
    public void ValidateToken_ReturnsFalse_WhenNotPaired()
    {
        Assert.False(_service.ValidateToken("any-token"));
    }

    [Fact]
    public void Unpair_ClearsState()
    {
        var token = _service.GeneratePairingToken();
        _service.ConfirmPairing(token);
        Assert.True(_service.IsPaired);

        _service.Unpair();
        Assert.False(_service.IsPaired);
        Assert.Null(_service.PairingToken);
    }

    [Fact]
    public void Certificate_HasCorrectProperties()
    {
        var cert = _service.Certificate;
        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void CertificateFingerprint_Is64Chars()
    {
        var fp = _service.CertificateFingerprint;
        Assert.Equal(64, fp.Length);
    }

    [Fact]
    public void Pairing_PersistsAcrossInstances()
    {
        var token = _service.GeneratePairingToken();
        _service.ConfirmPairing(token);

        // Create a new password manager (same config dir, unlock with same password)
        var pm2 = new PasswordManagerService(_config);
        pm2.Unlock("test-password-123");
        Assert.True(pm2.IsUnlocked);

        // Create a new pairing service with the unlocked password manager
        var service2 = new PairingService(_config, pm2);

        Assert.True(service2.IsPaired);
        Assert.Equal(token, service2.PairingToken);
    }

    [Fact]
    public void IsPaired_ReturnsFalse_WhenPasswordManagerLocked()
    {
        var token = _service.GeneratePairingToken();
        _service.ConfirmPairing(token);
        Assert.True(_service.IsPaired);

        // Lock the password manager
        _passwordManager.Lock();
        Assert.False(_passwordManager.IsUnlocked);

        // IsPaired should now return false since PairingToken comes from locked password manager
        Assert.False(_service.IsPaired);
        Assert.Null(_service.PairingToken);
    }

    [Fact]
    public void ServerIdentityKey_Available_WhenUnlocked()
    {
        Assert.True(_passwordManager.IsUnlocked);
        Assert.NotNull(_service.ServerIdentityKey);
        Assert.Equal(32, _service.ServerIdentityKey!.Length);
    }

    [Fact]
    public void ServerIdentityKey_Null_WhenLocked()
    {
        _passwordManager.Lock();
        Assert.Null(_service.ServerIdentityKey);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort */ }
    }
}
