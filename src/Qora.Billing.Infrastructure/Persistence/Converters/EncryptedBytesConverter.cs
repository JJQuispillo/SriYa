using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace Qora.Billing.Infrastructure.Persistence.Converters;

/// <summary>
/// EF Core value converter that transparently AES-256-encrypts byte[] columns at rest,
/// storing the result as Base64 text.
/// Format: [16-byte salt][16-byte IV][ciphertext] encoded as Base64.
/// Key derivation uses HKDF-SHA256 with a per-value random salt for security.
/// </summary>
public class EncryptedBytesConverter(string encryptionKey) : ValueConverter<byte[]?, string?>(
    v => v == null ? null : Encrypt(v, encryptionKey),
    v => v == null ? null : Decrypt(v, encryptionKey))
{
    private static string Encrypt(byte[] plainBytes, string key)
    {
        // Generate random 16-byte salt for HKDF key derivation
        var salt = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key, salt);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Format: [16-byte salt][16-byte IV][ciphertext]
        var result = new byte[salt.Length + aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(aes.IV, 0, result, salt.Length, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, salt.Length + aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static byte[] Decrypt(string cipherText, string key)
    {
        var cipherBytes = Convert.FromBase64String(cipherText);

        const int saltLength = 16;
        const int ivLength = 16;

        var salt = new byte[saltLength];
        var iv = new byte[ivLength];
        var encrypted = new byte[cipherBytes.Length - saltLength - ivLength];

        Buffer.BlockCopy(cipherBytes, 0, salt, 0, saltLength);
        Buffer.BlockCopy(cipherBytes, saltLength, iv, 0, ivLength);
        Buffer.BlockCopy(cipherBytes, saltLength + ivLength, encrypted, 0, encrypted.Length);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key, salt);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    /// <summary>Encrypt non-nullable bytes. Used by non-nullable property configs.</summary>
    internal static string EncryptValue(byte[] plainBytes, string key) => Encrypt(plainBytes, key);

    /// <summary>Decrypt to non-nullable bytes. Used by non-nullable property configs.</summary>
    internal static byte[] DecryptValue(string cipherText, string key) => Decrypt(cipherText, key);

    /// <summary>
    /// Derives a 32-byte AES key from the master key and a per-value salt using HKDF-SHA256.
    /// </summary>
    private static byte[] DeriveKey(string key, byte[] salt) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(key),
            32,
            salt,
            null);
}
