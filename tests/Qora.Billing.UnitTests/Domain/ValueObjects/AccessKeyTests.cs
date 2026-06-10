using FluentAssertions;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.UnitTests.Domain.ValueObjects;

public class AccessKeyTests
{
    private static string GenerateValidAccessKey()
    {
        var baseDigits = "180320260117922680710011001001000000012372816811";

        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = baseDigits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (baseDigits.Length - 1 - i) % weights.Length;
            sum += (baseDigits[i] - '0') * weights[weightIndex];
        }
        var remainder = sum % 11;
        var checkDigit = 11 - remainder;
        checkDigit = checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };

        return baseDigits + checkDigit;
    }

    [Fact]
    public void Constructor_WithValidAccessKey_ShouldSucceed()
    {
        var validKey = GenerateValidAccessKey();

        var accessKey = new AccessKey(validKey);

        accessKey.Value.Should().Be(validKey);
    }

    [Fact]
    public void Constructor_WithValidAccessKey_ShouldReturnValueInToString()
    {
        var validKey = GenerateValidAccessKey();

        var accessKey = new AccessKey(validKey);

        accessKey.ToString().Should().Be(validKey);
    }

    [Fact]
    public void Constructor_WithEmptyString_ShouldThrowInvalidAccessKeyException()
    {
        var act = () => new AccessKey("");

        act.Should().Throw<InvalidAccessKeyException>()
            .WithMessage("*vacía*");
    }

    [Fact]
    public void Constructor_WithNull_ShouldThrowInvalidAccessKeyException()
    {
        var act = () => new AccessKey(null!);

        act.Should().Throw<InvalidAccessKeyException>()
            .WithMessage("*vacía*");
    }

    [Fact]
    public void Constructor_WithWrongLength_ShouldThrowInvalidAccessKeyException()
    {
        var act = () => new AccessKey("12345678901234567890");

        act.Should().Throw<InvalidAccessKeyException>()
            .WithMessage("*49 dígitos*");
    }

    [Fact]
    public void Constructor_WithNonDigitCharacters_ShouldThrowInvalidAccessKeyException()
    {
        var act = () => new AccessKey("18032026011792268071001100100100000001237281681A1");

        act.Should().Throw<InvalidAccessKeyException>()
            .WithMessage("*solo dígitos*");
    }

    [Fact]
    public void Constructor_WithInvalidCheckDigit_ShouldThrowInvalidAccessKeyException()
    {
        var validKey = GenerateValidAccessKey();
        var lastDigit = validKey[^1] - '0';
        var invalidLastDigit = (lastDigit + 1) % 10;
        var invalidKey = validKey[..48] + invalidLastDigit;

        var act = () => new AccessKey(invalidKey);

        act.Should().Throw<InvalidAccessKeyException>()
            .WithMessage("*Mod11*");
    }

    [Fact]
    public void TwoAccessKeys_WithSameValue_ShouldBeEqual()
    {
        var validKey = GenerateValidAccessKey();

        var key1 = new AccessKey(validKey);
        var key2 = new AccessKey(validKey);

        (key1 == key2).Should().BeTrue();
    }
}
