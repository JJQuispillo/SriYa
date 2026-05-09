using FluentAssertions;
using Qora.Billing.Infrastructure.Persistence.Converters;

namespace Qora.Billing.UnitTests.Infrastructure.Email;

public class EncryptedStringConverterTests
{
    private const string TestKey = "test-key-for-unit-tests-only!!!";

    [Fact]
    public void EncryptThenDecrypt_ShouldReturnOriginalValue()
    {
        // Arrange
        var converter = new EncryptedStringConverter(TestKey);
        var originalValue = "my-secret-smtp-password";

        // Act - use the converter functions (encrypt then decrypt)
        var encrypted = (string?)converter.ConvertToProvider.Invoke(originalValue);
        var decrypted = (string?)converter.ConvertFromProvider.Invoke(encrypted);

        // Assert
        decrypted.Should().Be(originalValue);
    }

    [Fact]
    public void Encrypt_ShouldReturnBase64String()
    {
        // Arrange
        var converter = new EncryptedStringConverter(TestKey);

        // Act
        var encrypted = (string?)converter.ConvertToProvider.Invoke("test-password");

        // Assert
        encrypted.Should().NotBeNullOrEmpty();
        // Should be valid Base64
        var act = () => Convert.FromBase64String(encrypted!);
        act.Should().NotThrow();
    }

    [Fact]
    public void Encrypt_TwoCallsWithSameInput_ShouldProduceDifferentCipherTexts()
    {
        // Arrange - different IVs are used each time
        var converter = new EncryptedStringConverter(TestKey);
        var plainText = "same-password";

        // Act
        var encrypted1 = (string?)converter.ConvertToProvider.Invoke(plainText);
        var encrypted2 = (string?)converter.ConvertToProvider.Invoke(plainText);

        // Assert - IVs differ, so cipher texts differ
        encrypted1.Should().NotBe(encrypted2);
    }

    [Fact]
    public void ConvertToProvider_WithNullInput_ShouldReturnNull()
    {
        // Arrange
        var converter = new EncryptedStringConverter(TestKey);

        // Act
        var result = converter.ConvertToProvider.Invoke(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertFromProvider_WithNullInput_ShouldReturnNull()
    {
        // Arrange
        var converter = new EncryptedStringConverter(TestKey);

        // Act
        var result = converter.ConvertFromProvider.Invoke(null);

        // Assert
        Assert.Null(result);
    }
}
