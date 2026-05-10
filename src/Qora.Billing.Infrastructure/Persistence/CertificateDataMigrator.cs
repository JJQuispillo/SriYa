using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// One-time data migrator that re-encrypts electronic_signatures rows after migration B1d runs.
///
/// Called from Program.cs startup after db.Database.MigrateAsync() completes.
/// Safe to call multiple times — rows already in the encrypted HKDF format are detected
/// and skipped (idempotent).
///
/// Why here and not inside the EF migration?
///   EF Core migrations run SQL, not arbitrary C# code. AES+HKDF requires .NET crypto
///   APIs not available in PL/pgSQL. The startup-hook pattern is the standard approach
///   for C# data migrations in production ASP.NET Core services.
/// </summary>
public class CertificateDataMigrator
{
    private readonly BillingDbContext _db;
    private readonly string _encryptionKey;
    private readonly ILogger<CertificateDataMigrator> _logger;

    public CertificateDataMigrator(
        BillingDbContext db,
        IConfiguration configuration,
        ILogger<CertificateDataMigrator> logger)
    {
        _db = db;
        _encryptionKey = configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key configuration is required.");
        _logger = logger;
    }

    /// <summary>
    /// Re-encrypts any electronic_signatures rows whose certificate_data or password_encrypted
    /// columns are not yet in the HKDF-encrypted Base64 format.
    ///
    /// Detection heuristic:
    ///   A valid HKDF-encrypted blob is Base64-encoded and at least 64 chars
    ///   ([16 salt] + [16 IV] + [≥1 AES block] → ≥48 bytes → 64 Base64 chars).
    ///   Values shorter than 64 chars, or that fail Base64 decode, are treated as unencrypted.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // Use raw SQL to avoid the EF converter decrypting values we haven't encrypted yet.
        // We need the raw column values, not the decrypted domain values.
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Read all rows as raw strings (bypassing EF value converters).
            var rows = new List<(Guid Id, string CertData, string PwdEncrypted)>();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, certificate_data, password_encrypted FROM electronic_signatures";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var certData = reader.GetString(1);
                    var pwdEncrypted = reader.GetString(2);
                    rows.Add((id, certData, pwdEncrypted));
                }
            }

            if (rows.Count == 0)
            {
                _logger.LogDebug("CertificateDataMigrator: no rows found — nothing to migrate.");
                return;
            }

            _logger.LogInformation(
                "CertificateDataMigrator: found {Count} electronic_signatures row(s). Checking for unencrypted data.",
                rows.Count);

            int migrated = 0;

            foreach (var (id, rawCertData, rawPwdEncrypted) in rows)
            {
                bool certNeedsMigration = !IsHkdfEncrypted(rawCertData);
                bool pwdNeedsMigration = !IsHkdfEncrypted(rawPwdEncrypted);

                if (!certNeedsMigration && !pwdNeedsMigration)
                    continue;

                string? newCertData = null;
                string? newPwdEncrypted = null;

                if (certNeedsMigration)
                {
                    // After ALTER COLUMN bytea→text, Postgres represents the old bytea value
                    // as a hex-escaped string like "\x504b030414..." (bytea hex format).
                    // Parse it back to raw bytes, then encrypt.
                    byte[] certBytes;
                    if (rawCertData.StartsWith("\\x", StringComparison.OrdinalIgnoreCase))
                    {
                        certBytes = ParsePostgresBytea(rawCertData);
                    }
                    else
                    {
                        // Might already be raw base64 or plain text — encode as-is to bytes.
                        certBytes = Convert.FromBase64String(rawCertData);
                    }
                    newCertData = EncryptBytes(certBytes, _encryptionKey);
                    _logger.LogDebug("CertificateDataMigrator: re-encrypting certificate_data for id={Id}", id);
                }

                if (pwdNeedsMigration)
                {
                    // password_encrypted was stored as plaintext before B1d.
                    newPwdEncrypted = EncryptString(rawPwdEncrypted, _encryptionKey);
                    _logger.LogDebug("CertificateDataMigrator: re-encrypting password_encrypted for id={Id}", id);
                }

                await using var updateCmd = connection.CreateCommand();

                if (certNeedsMigration && pwdNeedsMigration)
                {
                    updateCmd.CommandText =
                        "UPDATE electronic_signatures SET certificate_data = @cert, password_encrypted = @pwd WHERE id = @id";
                    var pCert = updateCmd.CreateParameter(); pCert.ParameterName = "@cert"; pCert.Value = newCertData!;
                    var pPwd = updateCmd.CreateParameter(); pPwd.ParameterName = "@pwd"; pPwd.Value = newPwdEncrypted!;
                    var pId = updateCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = id;
                    updateCmd.Parameters.Add(pCert);
                    updateCmd.Parameters.Add(pPwd);
                    updateCmd.Parameters.Add(pId);
                }
                else if (certNeedsMigration)
                {
                    updateCmd.CommandText =
                        "UPDATE electronic_signatures SET certificate_data = @cert WHERE id = @id";
                    var pCert = updateCmd.CreateParameter(); pCert.ParameterName = "@cert"; pCert.Value = newCertData!;
                    var pId = updateCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = id;
                    updateCmd.Parameters.Add(pCert);
                    updateCmd.Parameters.Add(pId);
                }
                else
                {
                    updateCmd.CommandText =
                        "UPDATE electronic_signatures SET password_encrypted = @pwd WHERE id = @id";
                    var pPwd = updateCmd.CreateParameter(); pPwd.ParameterName = "@pwd"; pPwd.Value = newPwdEncrypted!;
                    var pId = updateCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = id;
                    updateCmd.Parameters.Add(pPwd);
                    updateCmd.Parameters.Add(pId);
                }

                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                migrated++;
            }

            if (migrated > 0)
                _logger.LogInformation(
                    "CertificateDataMigrator: successfully re-encrypted {Count} row(s).", migrated);
            else
                _logger.LogDebug("CertificateDataMigrator: all rows already encrypted — nothing to do.");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Heuristic: a value is HKDF-encrypted if it is valid Base64 and decodes to ≥48 bytes
    /// (16 salt + 16 IV + ≥16 ciphertext = minimum 48 bytes = 64 Base64 chars).
    /// </summary>
    private static bool IsHkdfEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 64) return false;
        // Bytea hex-escaped strings start with \x — definitely not Base64-encrypted.
        if (value.StartsWith("\\x", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= 48;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a Postgres bytea hex-escaped string ("\x504b0304...") to a byte array.
    /// </summary>
    private static byte[] ParsePostgresBytea(string hex)
    {
        // Strip the \x prefix
        var hexStr = hex.Substring(2);
        var bytes = new byte[hexStr.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hexStr.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static byte[] DeriveKey(string masterKey, byte[] salt) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(masterKey),
            32,
            salt,
            null);

    private static string EncryptBytes(byte[] plainBytes, string masterKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(masterKey, salt);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[salt.Length + aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(aes.IV, 0, result, salt.Length, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, salt.Length + aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string EncryptString(string plainText, string masterKey) =>
        EncryptBytes(Encoding.UTF8.GetBytes(plainText), masterKey);
}
