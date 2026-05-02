using Xunit;
using Phobri.Desktop.Services;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.Tests.Services;

public sealed class PairingServiceTests : IDisposable
{
    private readonly string _testDir;

    public PairingServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "phobri-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void NewService_IsNotPaired()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        Assert.False(service.IsPaired);
        Assert.Null(service.PairingToken);
    }

    [Fact]
    public void GeneratePairingToken_Returns64CharHex()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var token = service.GeneratePairingToken();

        Assert.NotNull(token);
        Assert.Equal(64, token.Length);
        // All hex characters
        Assert.All(token, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void GeneratePairingToken_DoesNotSetPaired()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        service.GeneratePairingToken();

        // Generating a token does not pair - confirmation is separate
        Assert.False(service.IsPaired);
    }

    [Fact]
    public void ConfirmPairing_SetsPairedState()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var token = service.GeneratePairingToken();
        var result = service.ConfirmPairing(token);

        Assert.True(result);
        Assert.True(service.IsPaired);
        Assert.Equal(token, service.PairingToken);
    }

    [Fact]
    public void ConfirmPairing_RejectsInvalidToken()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var result = service.ConfirmPairing("not-valid-hex-64");

        Assert.False(result);
        Assert.False(service.IsPaired);
    }

    [Fact]
    public void ValidateToken_ReturnsTrue_ForCorrectToken()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var token = service.GeneratePairingToken();
        service.ConfirmPairing(token);

        Assert.True(service.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_ReturnsFalse_ForWrongToken()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var token = service.GeneratePairingToken();
        service.ConfirmPairing(token);

        Assert.False(service.ValidateToken("wrong-token"));
    }

    [Fact]
    public void ValidateToken_ReturnsFalse_WhenNotPaired()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        Assert.False(service.ValidateToken("any-token"));
    }

    [Fact]
    public void Unpair_ClearsState()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var token = service.GeneratePairingToken();
        service.ConfirmPairing(token);
        Assert.True(service.IsPaired);

        service.Unpair();
        Assert.False(service.IsPaired);
        Assert.Null(service.PairingToken);
    }

    [Fact]
    public void Certificate_HasCorrectProperties()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var cert = service.Certificate;
        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void CertificateFingerprint_Is64Chars()
    {
        var config = new ConfigurationManager(_testDir);
        var service = new PairingService(config);

        var fp = service.CertificateFingerprint;
        Assert.Equal(64, fp.Length);
    }

    [Fact]
    public void Pairing_PersistsAcrossInstances()
    {
        var config1 = new ConfigurationManager(_testDir);
        var service1 = new PairingService(config1);
        var token = service1.GeneratePairingToken();
        service1.ConfirmPairing(token);

        // Create a new service with the same config dir
        var config2 = new ConfigurationManager(_testDir);
        var service2 = new PairingService(config2);

        Assert.True(service2.IsPaired);
        Assert.Equal(token, service2.PairingToken);
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
