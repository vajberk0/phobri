using Xunit;
using Phobri.Desktop.Infrastructure;

namespace Phobri.Desktop.Tests.Infrastructure;

public sealed class CryptoServiceTests : IDisposable
{
    private readonly string _testDir;

    public CryptoServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "phobri-crypto-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void GenerateKey_Produces32Bytes()
    {
        var key = CryptoService.GenerateKey();
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void GenerateKey_ProducesUniqueKeys()
    {
        var key1 = CryptoService.GenerateKey();
        var key2 = CryptoService.GenerateKey();
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKey_ProducesConsistentResults()
    {
        var salt = CryptoService.GenerateSalt();
        var key1 = CryptoService.DeriveKey("password", salt);
        var key2 = CryptoService.DeriveKey("password", salt);
        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length);
    }

    [Fact]
    public void DeriveKey_DifferentPasswordsProduceDifferentKeys()
    {
        var salt = CryptoService.GenerateSalt();
        var key1 = CryptoService.DeriveKey("password1", salt);
        var key2 = CryptoService.DeriveKey("password2", salt);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKey_DifferentSaltsProduceDifferentKeys()
    {
        var salt1 = CryptoService.GenerateSalt();
        var salt2 = CryptoService.GenerateSalt();
        var key1 = CryptoService.DeriveKey("password", salt1);
        var key2 = CryptoService.DeriveKey("password", salt2);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void EncryptDecryptString_RoundTrip()
    {
        var key = CryptoService.GenerateKey();
        var original = "Hello, Phobri! This is a test message with Unicode: 🍕🎉";

        var encrypted = CryptoService.EncryptString(original, key);
        Assert.NotEqual(original, encrypted); // Should be different

        var decrypted = CryptoService.DecryptString(encrypted, key);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void DecryptString_WrongKey_ThrowsException()
    {
        var key1 = CryptoService.GenerateKey();
        var key2 = CryptoService.GenerateKey();
        var encrypted = CryptoService.EncryptString("test", key1);

        // Wrong key should throw either CryptographicException or AuthenticationTagMismatchException
        Assert.ThrowsAny<Exception>(() => CryptoService.DecryptString(encrypted, key2));
    }

    [Fact]
    public void EncryptDecryptBytes_RoundTrip()
    {
        var key = CryptoService.GenerateKey();
        var original = new byte[] { 0x53, 0x51, 0x4c, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x33, 0x00, 0x01, 0x02, 0x03 };

        var encrypted = CryptoService.EncryptBytes(original, key);
        Assert.NotEqual(original, encrypted);

        var decrypted = CryptoService.DecryptBytes(encrypted, key);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public async Task EncryptDecryptFile_RoundTrip()
    {
        var key = CryptoService.GenerateKey();
        var originalPath = Path.Combine(_testDir, "original.bin");
        var encryptedPath = Path.Combine(_testDir, "encrypted.bin");
        var decryptedPath = Path.Combine(_testDir, "decrypted.bin");

        // Create a test file with SQLite-like header
        var originalData = new byte[4096];
        Random.Shared.NextBytes(originalData);
        // Add SQLite header bytes
        var sqliteHeader = new byte[] { 0x53, 0x51, 0x4c, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x33, 0x00 };
        Buffer.BlockCopy(sqliteHeader, 0, originalData, 0, sqliteHeader.Length);
        await File.WriteAllBytesAsync(originalPath, originalData);

        await CryptoService.EncryptFileAsync(originalPath, encryptedPath, key);
        Assert.True(File.Exists(encryptedPath));
        Assert.NotEqual(originalData.Length, new FileInfo(encryptedPath).Length); // Should be longer due to nonce+tag

        await CryptoService.DecryptFileAsync(encryptedPath, decryptedPath, key);
        Assert.True(File.Exists(decryptedPath));

        var decryptedData = await File.ReadAllBytesAsync(decryptedPath);
        Assert.Equal(originalData, decryptedData);
    }

    [Fact]
    public void ComputeHmacSha256Hex_Consistent()
    {
        var key = CryptoService.GenerateKey();
        var message = "test-nonce|1234567890";

        var hmac1 = CryptoService.ComputeHmacSha256Hex(key, message);
        var hmac2 = CryptoService.ComputeHmacSha256Hex(key, message);

        Assert.Equal(hmac1, hmac2);
        Assert.Equal(64, hmac1.Length); // Hex-encoded SHA-256 = 64 chars
    }

    [Fact]
    public void ComputeHmacSha256Hex_DifferentKey_DifferentResult()
    {
        var key1 = CryptoService.GenerateKey();
        var key2 = CryptoService.GenerateKey();
        var message = "test";

        var hmac1 = CryptoService.ComputeHmacSha256Hex(key1, message);
        var hmac2 = CryptoService.ComputeHmacSha256Hex(key2, message);

        Assert.NotEqual(hmac1, hmac2);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }
}
