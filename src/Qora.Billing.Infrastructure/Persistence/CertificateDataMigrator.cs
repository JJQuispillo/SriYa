using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// Migrador de datos de una sola vez que vuelve a cifrar las filas de electronic_signatures después de que se ejecuta la migración B1d.
///
/// Se invoca desde el arranque en Program.cs después de que db.Database.MigrateAsync() finaliza.
/// Es seguro llamarlo varias veces — las filas que ya están en el formato cifrado HKDF se detectan
/// y se omiten (idempotente).
///
/// ¿Por qué aquí y no dentro de la migración de EF?
///   Las migraciones de EF Core ejecutan SQL, no código C# arbitrario. AES+HKDF requiere las APIs
///   de criptografía de .NET que no están disponibles en PL/pgSQL. El patrón de startup-hook es el enfoque
///   estándar para las migraciones de datos en C# en servicios ASP.NET Core en producción.
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
    /// Vuelve a cifrar cualquier fila de electronic_signatures cuyas columnas certificate_data o password_encrypted
    /// no estén todavía en el formato Base64 cifrado con HKDF.
    ///
    /// Heurística de detección:
    ///   Un blob válido cifrado con HKDF está codificado en Base64 y tiene al menos 64 caracteres
    ///   ([16 salt] + [16 IV] + [≥1 bloque AES] → ≥48 bytes → 64 caracteres Base64).
    ///   Los valores con menos de 64 caracteres, o que fallan al decodificar Base64, se tratan como no cifrados.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // Usar SQL en crudo para evitar que el conversor de EF descifre valores que aún no hemos cifrado.
        // Necesitamos los valores crudos de las columnas, no los valores de dominio descifrados.
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Leer todas las filas como cadenas crudas (saltándose los value converters de EF).
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
                    // Después de ALTER COLUMN bytea→text, Postgres representa el valor bytea antiguo
                    // como una cadena hex-escapada tipo "\x504b030414..." (formato hex de bytea).
                    // Parsearlo de vuelta a bytes crudos y luego cifrar.
                    byte[] certBytes;
                    if (rawCertData.StartsWith("\\x", StringComparison.OrdinalIgnoreCase))
                    {
                        certBytes = ParsePostgresBytea(rawCertData);
                    }
                    else
                    {
                        // Podría ya ser base64 en crudo o texto plano — codificar tal cual a bytes.
                        certBytes = Convert.FromBase64String(rawCertData);
                    }
                    newCertData = EncryptBytes(certBytes, _encryptionKey);
                    _logger.LogDebug("CertificateDataMigrator: re-encrypting certificate_data for id={Id}", id);
                }

                if (pwdNeedsMigration)
                {
                    // password_encrypted se almacenaba como texto plano antes de B1d.
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

    // ── Auxiliares ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Heurística: un valor está cifrado con HKDF si es Base64 válido y decodifica a ≥48 bytes
    /// (16 salt + 16 IV + ≥16 de texto cifrado = mínimo 48 bytes = 64 caracteres Base64).
    /// </summary>
    private static bool IsHkdfEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 64) return false;
        // Las cadenas bytea hex-escapadas empiezan con \x — definitivamente no están cifradas en Base64.
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
    /// Parsea una cadena bytea hex-escapada de Postgres ("\x504b0304...") a un arreglo de bytes.
    /// </summary>
    private static byte[] ParsePostgresBytea(string hex)
    {
        // Quitar el prefijo \x
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
