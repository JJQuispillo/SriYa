using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace Qora.Billing.Infrastructure.Persistence.Converters;

/// <summary>
/// EF Core value converter that transparently AES-256-encrypts string columns at rest.
/// The IV is prepended to the cipher bytes and stored as Base64.
/// </summary>
public class EncryptedStringConverter(string encryptionKey) : ValueConverter<string?, string?>(
    v => v == null ? null : Encrypt(v, encryptionKey),
    v => v == null ? null : Decrypt(v, encryptionKey))
{
    private static string Encrypt(string plainText, string key)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string cipherText, string key)
    {
        var cipherBytes = Convert.FromBase64String(cipherText);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);

        var iv = new byte[aes.BlockSize / 8];
        var encrypted = new byte[cipherBytes.Length - iv.Length];

        Buffer.BlockCopy(cipherBytes, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(cipherBytes, iv.Length, encrypted, 0, encrypted.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var resultBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return Encoding.UTF8.GetString(resultBytes);
    }

    /// <summary>Pads or truncates key to exactly 32 bytes for AES-256.</summary>
    private static byte[] DeriveKey(string key) =>
        Encoding.UTF8.GetBytes(key.PadRight(32)[..32]);
}
