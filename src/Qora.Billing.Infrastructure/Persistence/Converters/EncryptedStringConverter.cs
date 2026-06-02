using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace Qora.Billing.Infrastructure.Persistence.Converters;

/// <summary>
/// Value converter de EF Core que cifra de forma transparente las columnas string en reposo con AES-256.
/// Formato: [salt de 16 bytes][IV de 16 bytes][texto cifrado] codificado en Base64.
/// La derivación de la clave usa HKDF-SHA256 con un salt aleatorio por valor por seguridad.
/// </summary>
public class EncryptedStringConverter(string encryptionKey) : ValueConverter<string?, string?>(
    v => v == null ? null : Encrypt(v, encryptionKey),
    v => v == null ? null : Decrypt(v, encryptionKey))
{
    private static string Encrypt(string plainText, string key)
    {
        // Generar un salt aleatorio de 16 bytes para la derivación de la clave HKDF
        var salt = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key, salt);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Formato: [salt de 16 bytes][IV de 16 bytes][texto cifrado]
        var result = new byte[salt.Length + aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(aes.IV, 0, result, salt.Length, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, salt.Length + aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string cipherText, string key)
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
        var resultBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return Encoding.UTF8.GetString(resultBytes);
    }

    /// <summary>Cifra un string no anulable. Usado por las configuraciones de propiedades no anulables.</summary>
    internal static string EncryptValue(string plainText, string key) => Encrypt(plainText, key);

    /// <summary>Descifra a un string no anulable. Usado por las configuraciones de propiedades no anulables.</summary>
    internal static string DecryptValue(string cipherText, string key) => Decrypt(cipherText, key);

    /// <summary>
    /// Deriva una clave AES de 32 bytes a partir de la clave maestra y un salt por valor usando HKDF-SHA256.
    /// </summary>
    private static byte[] DeriveKey(string key, byte[] salt) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(key),
            32,
            salt,
            null);
}
