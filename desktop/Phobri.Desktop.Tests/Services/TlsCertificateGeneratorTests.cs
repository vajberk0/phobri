using Xunit;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Tests.Services;

public sealed class TlsCertificateGeneratorTests
{
    [Fact]
    public void GenerateSelfSigned_CreatesValidCertificate()
    {
        var cert = TlsCertificateGenerator.GenerateSelfSigned();

        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey);
        Assert.Equal("CN=Phobri Desktop, O=Phobri, OU=Sync", cert.Subject);
    }

    [Fact]
    public void GetFingerprint_ReturnsConsistentHash()
    {
        var cert = TlsCertificateGenerator.GenerateSelfSigned();

        var fp1 = TlsCertificateGenerator.GetFingerprint(cert);
        var fp2 = TlsCertificateGenerator.GetFingerprint(cert);

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length); // SHA-256 hex = 64 chars
    }

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        var cert = TlsCertificateGenerator.GenerateSelfSigned();
        var path = Path.GetTempFileName();

        try
        {
            TlsCertificateGenerator.SaveToFile(cert, path);
            var loaded = TlsCertificateGenerator.LoadFromFile(path);

            Assert.NotNull(loaded);
            Assert.Equal(cert.Subject, loaded.Subject);
            Assert.True(loaded.HasPrivateKey);

            var fp1 = TlsCertificateGenerator.GetFingerprint(cert);
            var fp2 = TlsCertificateGenerator.GetFingerprint(loaded);
            Assert.Equal(fp1, fp2);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_ThrowsIfNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            TlsCertificateGenerator.LoadFromFile("/nonexistent/cert.pfx"));
    }

    [Fact]
    public void GeneratedCerts_AreUnique()
    {
        var cert1 = TlsCertificateGenerator.GenerateSelfSigned();
        var cert2 = TlsCertificateGenerator.GenerateSelfSigned();

        var fp1 = TlsCertificateGenerator.GetFingerprint(cert1);
        var fp2 = TlsCertificateGenerator.GetFingerprint(cert2);

        Assert.NotEqual(fp1, fp2);
    }
}
