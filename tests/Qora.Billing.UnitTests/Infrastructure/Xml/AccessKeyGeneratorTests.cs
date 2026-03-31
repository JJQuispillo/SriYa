using FluentAssertions;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class AccessKeyGeneratorTests
{
    private readonly DateTime _testDate = new(2026, 3, 18);
    private const string TestRuc = "1792268071001";
    private const string TestEstablishment = "001";
    private const string TestEmissionPoint = "001";
    private const string TestSequential = "000000012";
    private const string TestNumericCode = "37281681";

    [Fact]
    public void Generate_WithValidInputs_ShouldReturn49DigitAccessKey()
    {
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        accessKey.Value.Length.Should().Be(49);
        accessKey.Value.All(char.IsDigit).Should().BeTrue();
    }

    [Fact]
    public void Generate_ShouldProduceValidAccessKey()
    {
        // If AccessKey constructor succeeds, the Mod11 check digit is valid
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        // Should not throw — AccessKey validates Mod11 in constructor
        var validated = new AccessKey(accessKey.Value);
        validated.Value.Should().Be(accessKey.Value);
    }

    [Fact]
    public void Generate_ShouldStartWithDateInDdMmYyyyFormat()
    {
        var date = new DateTime(2026, 3, 18);

        var accessKey = AccessKeyGenerator.Generate(
            date,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        accessKey.Value[..8].Should().Be("18032026");
    }

    [Fact]
    public void Generate_WithFactura_ShouldHaveDocTypeCode01()
    {
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        // Positions 8-9 = document type code
        accessKey.Value.Substring(8, 2).Should().Be("01");
    }

    [Fact]
    public void Generate_WithProductionEnv_ShouldHaveEnvCode2()
    {
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Production,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        // Position 23 = environment code (after date:8 + docType:2 + ruc:13)
        accessKey.Value[23].Should().Be('2');
    }

    [Fact]
    public void Generate_WithTestEnv_ShouldHaveEnvCode1()
    {
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.Factura,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        accessKey.Value[23].Should().Be('1');
    }

    [Fact]
    public void Generate_WithInvalidRucLength_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.Generate(
            _testDate, DocumentType.Factura, "12345",
            EnvironmentType.Test, TestEstablishment, TestEmissionPoint,
            TestSequential, TestNumericCode, EmissionType.Normal);

        act.Should().Throw<ArgumentException>().WithMessage("*RUC*13*");
    }

    [Fact]
    public void Generate_WithInvalidEstablishmentLength_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.Generate(
            _testDate, DocumentType.Factura, TestRuc,
            EnvironmentType.Test, "01", TestEmissionPoint,
            TestSequential, TestNumericCode, EmissionType.Normal);

        act.Should().Throw<ArgumentException>().WithMessage("*Establishment*3*");
    }

    [Fact]
    public void Generate_WithInvalidSequentialLength_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.Generate(
            _testDate, DocumentType.Factura, TestRuc,
            EnvironmentType.Test, TestEstablishment, TestEmissionPoint,
            "123", TestNumericCode, EmissionType.Normal);

        act.Should().Throw<ArgumentException>().WithMessage("*Sequential*9*");
    }

    [Fact]
    public void Generate_WithInvalidNumericCodeLength_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.Generate(
            _testDate, DocumentType.Factura, TestRuc,
            EnvironmentType.Test, TestEstablishment, TestEmissionPoint,
            TestSequential, "123", EmissionType.Normal);

        act.Should().Throw<ArgumentException>().WithMessage("*Numeric code*8*");
    }

    [Fact]
    public void CalculateMod11CheckDigit_WithKnownInput_ShouldReturnCorrectDigit()
    {
        // Use the same base digits from AccessKeyTests
        var baseDigits = "180320260117922680710011001001000000012372816811";

        var checkDigit = AccessKeyGenerator.CalculateMod11CheckDigit(baseDigits);

        // Calculate expected via same algorithm
        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = baseDigits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (baseDigits.Length - 1 - i) % weights.Length;
            sum += (baseDigits[i] - '0') * weights[weightIndex];
        }
        var remainder = sum % 11;
        var expected = 11 - remainder;
        expected = expected switch { 11 => 0, 10 => 1, _ => expected };

        checkDigit.Should().Be(expected);
    }

    [Fact]
    public void CalculateMod11CheckDigit_WithWrongLength_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.CalculateMod11CheckDigit("12345");

        act.Should().Throw<ArgumentException>().WithMessage("*48*");
    }

    [Fact]
    public void GenerateNumericCode_ShouldReturn8Digits()
    {
        var code = AccessKeyGenerator.GenerateNumericCode();

        code.Length.Should().Be(8);
        code.All(char.IsDigit).Should().BeTrue();
    }

    [Fact]
    public void Generate_WithNullRuc_ShouldThrow()
    {
        var act = () => AccessKeyGenerator.Generate(
            _testDate, DocumentType.Factura, null!,
            EnvironmentType.Test, TestEstablishment, TestEmissionPoint,
            TestSequential, TestNumericCode, EmissionType.Normal);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Generate_WithNotaCredito_ShouldHaveDocTypeCode04()
    {
        var accessKey = AccessKeyGenerator.Generate(
            _testDate,
            DocumentType.NotaCredito,
            TestRuc,
            EnvironmentType.Test,
            TestEstablishment,
            TestEmissionPoint,
            TestSequential,
            TestNumericCode,
            EmissionType.Normal);

        accessKey.Value.Substring(8, 2).Should().Be("04");
    }
}
