using FluentAssertions;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.UnitTests.Domain.ValueObjects;

public class RucTests
{
    [Theory]
    [InlineData("1792268071001")]
    [InlineData("0102030405001")]
    [InlineData("2400000000001")]
    [InlineData("3000000000001")]
    public void Constructor_WithValidRuc_ShouldSucceed(string value)
    {
        var ruc = new Ruc(value);

        ruc.Value.Should().Be(value);
    }

    [Fact]
    public void Constructor_WithValidRuc_ShouldReturnValueInToString()
    {
        var ruc = new Ruc("1792268071001");

        ruc.ToString().Should().Be("1792268071001");
    }

    [Fact]
    public void Constructor_WithEmptyString_ShouldThrowInvalidRucException()
    {
        var act = () => new Ruc("");

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void Constructor_WithNull_ShouldThrowInvalidRucException()
    {
        var act = () => new Ruc(null!);

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void Constructor_WithWrongLength_ShouldThrowInvalidRucException()
    {
        var act = () => new Ruc("12345");

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*13 digits*");
    }

    [Fact]
    public void Constructor_WithNonDigits_ShouldThrowInvalidRucException()
    {
        var act = () => new Ruc("179226807A001");

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*only digits*");
    }

    [Fact]
    public void Constructor_WithoutTrailing001_ShouldThrowInvalidRucException()
    {
        var act = () => new Ruc("1792268071002");

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*001*");
    }

    [Theory]
    [InlineData("0092268071001")]
    [InlineData("2592268071001")]
    [InlineData("9992268071001")]
    public void Constructor_WithInvalidProvinceCode_ShouldThrowInvalidRucException(string value)
    {
        var act = () => new Ruc(value);

        act.Should().Throw<InvalidRucException>()
            .WithMessage("*province code*");
    }

    [Fact]
    public void TwoRucs_WithSameValue_ShouldBeEqual()
    {
        var ruc1 = new Ruc("1792268071001");
        var ruc2 = new Ruc("1792268071001");

        (ruc1 == ruc2).Should().BeTrue();
    }
}
