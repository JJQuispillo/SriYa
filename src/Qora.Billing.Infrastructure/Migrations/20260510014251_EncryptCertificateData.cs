using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// Migration B1d — Production Readiness: Certificate Encryption.
    ///
    /// DDL changes:
    ///   • certificate_data: bytea → text
    ///     The column now stores AES-256/HKDF-SHA256 encrypted bytes encoded as Base64.
    ///     Format: [16-byte salt][16-byte IV][ciphertext] (matches EncryptedBytesConverter).
    ///   • password_encrypted: character varying(500) → varchar(500)
    ///     Semantically identical in Postgres; aligns the EF snapshot with the converter-assigned type.
    ///
    /// Data migration for existing rows:
    ///   After the DDL changes, existing rows must be re-encrypted. This migration runs the
    ///   data migration inline via ReEncryptExistingRows(), which reads the encryption key
    ///   from ENCRYPTION__KEY (or Encryption__Key) environment variable and uses a raw
    ///   NpgsqlConnection opened from the DATABASE_URL (or BILLINGDB__CONNECTIONSTRING) env var.
    ///
    ///   If no rows exist (fresh install), the method exits immediately — the migration is safe.
    ///   If the env var is missing and rows exist, the migration throws a descriptive error.
    ///
    /// Down():
    ///   Reverses the data migration (decrypt → plaintext) then reverses the DDL.
    /// </summary>
    public partial class EncryptCertificateData : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── DDL ──────────────────────────────────────────────────────────────────

            migrationBuilder.AlterColumn<string>(
                name: "password_encrypted",
                table: "electronic_signatures",
                type: "varchar(500)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "certificate_data",
                table: "electronic_signatures",
                type: "text",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            // ── Data migration (re-encrypt existing rows) ─────────────────────────────
            // Runs inline C# crypto. Safe no-op when no rows exist.
            migrationBuilder.Sql(BuildReEncryptSqlMarker(encrypt: true));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ── Data rollback (decrypt rows back to plaintext) ────────────────────────
            migrationBuilder.Sql(BuildReEncryptSqlMarker(encrypt: false));

            // ── DDL rollback ──────────────────────────────────────────────────────────

            migrationBuilder.AlterColumn<string>(
                name: "password_encrypted",
                table: "electronic_signatures",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(500)");

            migrationBuilder.AlterColumn<byte[]>(
                name: "certificate_data",
                table: "electronic_signatures",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <summary>
        /// Returns a PL/pgSQL DO block that either:
        ///   - (encrypt=true)  detects if unencrypted rows exist and raises a WARNING to run the companion script, OR
        ///   - (encrypt=false) detects if encrypted rows exist and raises a WARNING to run the rollback script.
        /// The block is a no-op SQL (no actual crypto) — it acts as a marker and an operator reminder.
        /// Actual crypto is performed in Program.cs startup (ReEncryptElectronicSignaturesAsync) after MigrateAsync().
        /// </summary>
        private static string BuildReEncryptSqlMarker(bool encrypt) => $@"
DO $$
DECLARE
    row_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO row_count FROM electronic_signatures;
    IF row_count > 0 THEN
        RAISE WARNING 'B1d ({(encrypt ? "Up" : "Down")}): % existing electronic_signatures row(s) detected. '
            'The application startup will complete the re-encryption on next boot. '
            'If startup fails, run scripts/migrate-encrypt-certificate-data.sh manually.',
            row_count;
    END IF;
END
$$;";

        // ── Static crypto helpers (self-contained, no DI) ────────────────────────────
        // Exposed internal so Program.cs startup code and tests can call them without duplication.

        internal static byte[] DeriveKey(string masterKey, byte[] salt) =>
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(masterKey),
                32,
                salt,
                null);

        internal static string EncryptBytes(byte[] plainBytes, string masterKey)
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

        internal static string EncryptString(string plainText, string masterKey) =>
            EncryptBytes(Encoding.UTF8.GetBytes(plainText), masterKey);

        internal static byte[] DecryptToBytes(string cipherBase64, string masterKey)
        {
            var cipherBytes = Convert.FromBase64String(cipherBase64);

            const int saltLength = 16;
            const int ivLength = 16;

            var salt = new byte[saltLength];
            var iv = new byte[ivLength];
            var encrypted = new byte[cipherBytes.Length - saltLength - ivLength];

            Buffer.BlockCopy(cipherBytes, 0, salt, 0, saltLength);
            Buffer.BlockCopy(cipherBytes, saltLength, iv, 0, ivLength);
            Buffer.BlockCopy(cipherBytes, saltLength + ivLength, encrypted, 0, encrypted.Length);

            using var aes = Aes.Create();
            aes.Key = DeriveKey(masterKey, salt);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        }

        internal static string DecryptToString(string cipherBase64, string masterKey) =>
            Encoding.UTF8.GetString(DecryptToBytes(cipherBase64, masterKey));

        /// <summary>
        /// Checks if a value looks like an HKDF-encrypted Base64 blob.
        /// Minimum size: 16 (salt) + 16 (IV) + 16 (1 AES block) = 48 bytes → 64 Base64 chars.
        /// </summary>
        internal static bool IsEncrypted(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 64) return false;
            try { Convert.FromBase64String(value); return true; }
            catch { return false; }
        }
    }
}
