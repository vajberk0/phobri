using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Phobri.Desktop.Infrastructure;

/// <summary>
/// Generates self-signed X.509 certificates for TLS communication.
/// Used for Trust-on-First-Use (TOFU) pairing with Android.
/// </summary>
public static class TlsCertificateGenerator
{
    private const int RsaKeySize = 2048;

    /// <summary>
    /// Generate a self-signed certificate for localhost.
    /// </summary>
    /// <param name="notBefore">Certificate validity start.</param>
    /// <param name="notAfter">Certificate validity end (default 10 years).</param>
    /// <returns>X509Certificate2 with private key.</returns>
    public static X509Certificate2 GenerateSelfSigned(DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var end = notAfter ?? DateTimeOffset.UtcNow.AddYears(10);

        using var rsa = RSA.Create(RsaKeySize);

        var subject = new X500DistinguishedName("CN=Phobri Desktop, O=Phobri, OU=Sync");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Add SAN for localhost
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Add basic constraints for end-entity cert
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        // Add key usage
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // Add enhanced key usage for server auth
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], // serverAuthentication
                critical: true));

        var cert = request.CreateSelfSigned(start, end);

        // Export with private key as PFX
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Compute the SHA-256 fingerprint of a certificate.
    /// </summary>
    public static string GetFingerprint(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Save a certificate with private key to a PFX file.
    /// </summary>
    public static void SaveToFile(X509Certificate2 cert, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
    }

    /// <summary>
    /// Load a certificate with private key from a PFX file.
    /// </summary>
    public static X509Certificate2 LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Certificate file not found", path);

        return new X509Certificate2(path, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
