using System.Security.Cryptography;

namespace Phobri.Desktop.Infrastructure;

/// <summary>
/// Cryptographic primitives for Phobri's password-based security.
/// Provides key derivation (PBKDF2), AES-256-GCM encryption, and random key generation.
/// </summary>
public static class CryptoService
{
    // --- Constants ---
    public const int SaltLength = 32;
    public const int KeyLength = 32; // 256 bits
    public const int NonceLength = 12; // 96 bits for AES-GCM
    public const int TagLength = 16; // 128 bits authentication tag
    public const int Pbkdf2Iterations = 600_000; // OWASP 2023 recommendation

    // --- Key Derivation ---

    /// <summary>
    /// Derive a 256-bit key from a password and salt using PBKDF2-HMAC-SHA256.
    /// </summary>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations = Pbkdf2Iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyLength);
    }

    /// <summary>
    /// Generate a cryptographically random salt.
    /// </summary>
    public static byte[] GenerateSalt()
    {
        return RandomNumberGenerator.GetBytes(SaltLength);
    }

    /// <summary>
    /// Generate a cryptographically random key (for DEK, SIK, etc.).
    /// </summary>
    public static byte[] GenerateKey()
    {
        return RandomNumberGenerator.GetBytes(KeyLength);
    }

    // --- AES-256-GCM String Encryption ---

    /// <summary>
    /// Encrypt a string with AES-256-GCM.
    /// Returns a Base64-encoded blob containing nonce + ciphertext + tag.
    /// </summary>
    public static string EncryptString(string plaintext, byte[] key)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Format: nonce || ciphertext || tag
        var result = new byte[NonceLength + cipherBytes.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceLength);
        Buffer.BlockCopy(cipherBytes, 0, result, NonceLength, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, result, NonceLength + cipherBytes.Length, TagLength);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt a Base64-encoded blob (nonce + ciphertext + tag) with AES-256-GCM.
    /// </summary>
    public static string DecryptString(string encryptedBase64, byte[] key)
    {
        var data = Convert.FromBase64String(encryptedBase64);
        if (data.Length < NonceLength + TagLength)
            throw new CryptographicException("Encrypted data is too short.");

        var nonce = new byte[NonceLength];
        var tag = new byte[TagLength];
        var cipherLen = data.Length - NonceLength - TagLength;
        var cipherBytes = new byte[cipherLen];
        var plainBytes = new byte[cipherLen];

        Buffer.BlockCopy(data, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(data, NonceLength, cipherBytes, 0, cipherLen);
        Buffer.BlockCopy(data, NonceLength + cipherLen, tag, 0, TagLength);

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }

    // --- AES-256-GCM File Encryption ---

    /// <summary>
    /// Encrypt a file to another file using AES-256-GCM.
    /// The output format is: [nonce (12)] [ciphertext + tag].
    /// </summary>
    public static async Task EncryptFileAsync(string inputPath, string outputPath, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        var cipherBytes = new byte[inputBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, inputBytes, cipherBytes, tag);

        using var output = File.Create(outputPath);
        await output.WriteAsync(nonce);
        await output.WriteAsync(cipherBytes);
        await output.WriteAsync(tag);
    }

    /// <summary>
    /// Decrypt a file to another file using AES-256-GCM.
    /// </summary>
    public static async Task DecryptFileAsync(string inputPath, string outputPath, byte[] key)
    {
        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        if (inputBytes.Length < NonceLength + TagLength)
            throw new CryptographicException("Encrypted file is too short.");

        var nonce = new byte[NonceLength];
        var cipherLen = inputBytes.Length - NonceLength - TagLength;
        var cipherBytes = new byte[cipherLen];
        var tag = new byte[TagLength];
        var plainBytes = new byte[cipherLen];

        Buffer.BlockCopy(inputBytes, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(inputBytes, NonceLength, cipherBytes, 0, cipherLen);
        Buffer.BlockCopy(inputBytes, NonceLength + cipherLen, tag, 0, TagLength);

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        await File.WriteAllBytesAsync(outputPath, plainBytes);
    }

    /// <summary>
    /// Encrypt byte array and return encrypted bytes (nonce || ciphertext || tag).
    /// </summary>
    public static byte[] EncryptBytes(byte[] plainBytes, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[NonceLength + cipherBytes.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceLength);
        Buffer.BlockCopy(cipherBytes, 0, result, NonceLength, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, result, NonceLength + cipherBytes.Length, TagLength);

        return result;
    }

    /// <summary>
    /// Decrypt byte array (nonce || ciphertext || tag) and return plain bytes.
    /// </summary>
    public static byte[] DecryptBytes(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < NonceLength + TagLength)
            throw new CryptographicException("Encrypted data is too short.");

        var nonce = new byte[NonceLength];
        var cipherLen = encryptedData.Length - NonceLength - TagLength;
        var cipherBytes = new byte[cipherLen];
        var tag = new byte[TagLength];
        var plainBytes = new byte[cipherLen];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(encryptedData, NonceLength, cipherBytes, 0, cipherLen);
        Buffer.BlockCopy(encryptedData, NonceLength + cipherLen, tag, 0, TagLength);

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return plainBytes;
    }

    // --- HMAC (for challenge-response) ---

    /// <summary>
    /// Compute HMAC-SHA256 for challenge-response authentication.
    /// </summary>
    public static byte[] ComputeHmacSha256(byte[] key, byte[] message)
    {
        return HMACSHA256.HashData(key, message);
    }

    /// <summary>
    /// Compute HMAC-SHA256 over a string message, returning hex-encoded result.
    /// </summary>
    public static string ComputeHmacSha256Hex(byte[] key, string message)
    {
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var hmac = HMACSHA256.HashData(key, messageBytes);
        return Convert.ToHexStringLower(hmac);
    }

    /// <summary>
    /// Constant-time comparison of two byte arrays.
    /// </summary>
    public static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
