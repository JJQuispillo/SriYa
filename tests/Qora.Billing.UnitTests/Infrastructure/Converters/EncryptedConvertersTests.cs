using FluentAssertions;
using Qora.Billing.Infrastructure.Persistence.Converters;

namespace Qora.Billing.UnitTests.Infrastructure.Converters;

public class EncryptedConvertersTests
{
    private const string TestKey = "my-super-secret-encryption-key-for-tests!";
    private const string AnotherKey = "another-completely-different-key-for-tests!";

    // Helper: use the internal static encrypt/decrypt methods directly for typed access.
    // For EncryptedStringConverter:
    private static string EncryptString(string plaintext, string key) =>
        EncryptedStringConverter.EncryptValue(plaintext, key);

    private static string DecryptString(string ciphertext, string key) =>
        EncryptedStringConverter.DecryptValue(ciphertext, key);

    // For EncryptedBytesConverter:
    private static string EncryptBytes(byte[] plainBytes, string key) =>
        EncryptedBytesConverter.EncryptValue(plainBytes, key);

    private static byte[] DecryptBytes(string ciphertext, string key) =>
        EncryptedBytesConverter.DecryptValue(ciphertext, key);

    // ─── EncryptedStringConverter ───────────────────────────────────────────

    [Fact]
    public void EncryptedStringConverter_RoundTrip_ReturnOriginalPlaintext()
    {
        // Arrange
        const string original = "plaintext-value-to-encrypt";

        // Act
        var encrypted = EncryptString(original, TestKey);
        var decrypted = DecryptString(encrypted, TestKey);

        // Assert
        decrypted.Should().Be(original);
    }

    [Fact]
    public void EncryptedStringConverter_NullInput_ReturnsNull()
    {
        // Arrange
        var converter = new EncryptedStringConverter(TestKey);

        // Act & Assert — the ValueConverter handles null internally
        ((string?)converter.ConvertToProvider(null)).Should().BeNull();
        ((string?)converter.ConvertFromProvider(null)).Should().BeNull();
    }

    [Fact]
    public void EncryptedStringConverter_SamePlaintext_ProducesDifferentCiphertext_DueToRandomSalt()
    {
        // Arrange — random salt means identical plaintexts MUST produce different ciphertexts
        const string plaintext = "same-value";

        // Act
        var cipher1 = EncryptString(plaintext, TestKey);
        var cipher2 = EncryptString(plaintext, TestKey);

        // Assert
        cipher1.Should().NotBe(cipher2, "each encryption uses a fresh random salt and IV");
    }

    [Fact]
    public void EncryptedStringConverter_DifferentKeys_ProduceDifferentCiphertext()
    {
        // Arrange
        const string plaintext = "hello";

        // Act
        var cipher1 = EncryptString(plaintext, TestKey);
        var cipher2 = EncryptString(plaintext, AnotherKey);

        // Assert
        cipher1.Should().NotBe(cipher2);
    }

    [Fact]
    public void EncryptedStringConverter_DifferentKey_CannotDecrypt()
    {
        // Arrange
        var encrypted = EncryptString("secret", TestKey);

        // Act
        var act = () => DecryptString(encrypted, AnotherKey);

        // Assert — AES with wrong key produces garbage or throws padding error
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EncryptedStringConverter_StoredValueIsValidBase64()
    {
        // Act
        var encrypted = EncryptString("test-value", TestKey);

        // Assert
        var act = () => Convert.FromBase64String(encrypted);
        act.Should().NotThrow();
    }

    [Fact]
    public void EncryptedStringConverter_StoredValue_HasExpectedMinimumLength()
    {
        // Act — stored format: [16-byte salt][16-byte IV][ciphertext] → minimum 32 bytes before Base64
        var encrypted = EncryptString("x", TestKey);
        var decoded = Convert.FromBase64String(encrypted);

        // Assert
        decoded.Length.Should().BeGreaterThan(32, "must contain at least salt + IV");
    }

    // ─── EncryptedBytesConverter ────────────────────────────────────────────

    [Fact]
    public void EncryptedBytesConverter_RoundTrip_ReturnOriginalBytes()
    {
        // Arrange
        var original = new byte[] { 0x01, 0x02, 0x03, 0xAB, 0xCD, 0xEF, 0xFF, 0x00 };

        // Act
        var encrypted = EncryptBytes(original, TestKey);
        var decrypted = DecryptBytes(encrypted, TestKey);

        // Assert
        decrypted.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void EncryptedBytesConverter_NullInput_ReturnsNull()
    {
        // Arrange
        var converter = new EncryptedBytesConverter(TestKey);

        // Act & Assert — the ValueConverter handles null internally
        ((string?)converter.ConvertToProvider(null)).Should().BeNull();
        ((byte[]?)converter.ConvertFromProvider(null)).Should().BeNull();
    }

    [Fact]
    public void EncryptedBytesConverter_SameBytes_ProducesDifferentCiphertext_DueToRandomSalt()
    {
        // Arrange
        var plainBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // Act
        var cipher1 = EncryptBytes(plainBytes, TestKey);
        var cipher2 = EncryptBytes(plainBytes, TestKey);

        // Assert
        cipher1.Should().NotBe(cipher2, "each encryption uses a fresh random salt and IV");
    }

    [Fact]
    public void EncryptedBytesConverter_DifferentKeys_ProduceDifferentCiphertext()
    {
        // Arrange
        var plainBytes = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        var cipher1 = EncryptBytes(plainBytes, TestKey);
        var cipher2 = EncryptBytes(plainBytes, AnotherKey);

        // Assert
        cipher1.Should().NotBe(cipher2);
    }

    [Fact]
    public void EncryptedBytesConverter_DifferentKey_CannotDecrypt()
    {
        // Arrange
        var encrypted = EncryptBytes(new byte[] { 0xAA, 0xBB, 0xCC }, TestKey);

        // Act
        var act = () => DecryptBytes(encrypted, AnotherKey);

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EncryptedBytesConverter_StoredValueIsValidBase64()
    {
        // Act
        var encrypted = EncryptBytes(new byte[] { 0x01, 0x02, 0x03 }, TestKey);

        // Assert
        var act = () => Convert.FromBase64String(encrypted);
        act.Should().NotThrow();
    }

    [Fact]
    public void EncryptedBytesConverter_RoundTrip_WithCertificateSizedData()
    {
        // Arrange — simulate a real .pfx certificate blob (~4KB)
        var certData = new byte[4096];
        Random.Shared.NextBytes(certData);

        // Act
        var encrypted = EncryptBytes(certData, TestKey);
        var decrypted = DecryptBytes(encrypted, TestKey);

        // Assert
        decrypted.Should().BeEquivalentTo(certData);
    }
}
