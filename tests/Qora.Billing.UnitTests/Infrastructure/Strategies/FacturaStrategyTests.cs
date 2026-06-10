using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;

namespace Qora.Billing.UnitTests.Infrastructure.Strategies;

public class FacturaStrategyTests
{
    private readonly Mock<IXmlGenerator> _xmlGeneratorMock = new();
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly FacturaStrategy _strategy;

    public FacturaStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new FacturaStrategy(_xmlGeneratorMock.Object, _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidFactura(decimal unitPrice = 10m, decimal taxRate = 15m)
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CONSUMIDOR FINAL",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.Factura, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Item", 1, unitPrice, 0, taxRate, "2", "4"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeFactura()
    {
        _strategy.DocumentType.Should().Be(DocumentType.Factura);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidFactura();

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithNoItems_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.Factura,
            new Dictionary<string, string>
            {
                ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
                ["dirMatriz"] = "Quito", ["estab"] = "001",
                ["ptoEmi"] = "001", ["secuencial"] = "000000001"
            },
            new Dictionary<string, string>
            {
                ["tipoIdentificacion"] = "04", ["razonSocial"] = "BUYER",
                ["identificacion"] = "9999999999999"
            });

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("at least one"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidIvaRate_ShouldReturnError()
    {
        var doc = CreateValidFactura(taxRate: 8m); // 8% is not a valid SRI rate

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("invalid IVA rate") && e.Contains("8"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidIvaRates_ShouldReturnNoErrors()
    {
        // 0%, 5%, 12%, 15% are all valid
        var doc = CreateValidFactura(taxRate: 0m);
        var errors0 = await _strategy.ValidateDocumentAsync(doc);
        errors0.Should().BeEmpty();

        doc = CreateValidFactura(taxRate: 5m);
        var errors5 = await _strategy.ValidateDocumentAsync(doc);
        errors5.Should().BeEmpty();

        doc = CreateValidFactura(taxRate: 12m);
        var errors12 = await _strategy.ValidateDocumentAsync(doc);
        errors12.Should().BeEmpty();

        doc = CreateValidFactura(taxRate: 15m);
        var errors15 = await _strategy.ValidateDocumentAsync(doc);
        errors15.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithAmountOver200_WithoutBuyerId_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001"
        };

        // No buyer identification
        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "BUYER"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.Factura, issuerInfo, buyerInfo);
        // 250 * 1 = 250 > $200 threshold
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Expensive Item", 1, 250m, 0, 15m, "2", "4"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("identificacion") && e.Contains("200"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithAmountUnder200_WithoutBuyerId_ShouldNotError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "07",
            ["razonSocial"] = "CONSUMIDOR FINAL"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.Factura, issuerInfo, buyerInfo);
        // 50 * 1 + tax < $200
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Cheap Item", 1, 50m, 0, 15m, "2", "4"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().NotContain(e => e.Contains("identification") && e.Contains("200"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingIssuerFields_ShouldReturnErrors()
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.Factura,
            new Dictionary<string, string>(), // no issuer fields
            new Dictionary<string, string>
            {
                ["tipoIdentificacion"] = "04", ["razonSocial"] = "BUYER",
                ["identificacion"] = "9999999999999"
            });
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Item", 1, 10m, 0, 15m, "2", "4"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("ruc"));
        errors.Should().Contain(e => e.Contains("razonSocial"));
        errors.Should().Contain(e => e.Contains("dirMatriz"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithWrongDocumentType_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.NotaCredito,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("Factura") && e.Contains("NotaCredito"));
    }

    [Fact]
    public async Task BuildXmlAsync_ShouldDelegateToXmlGenerator()
    {
        var doc = CreateValidFactura();
        _xmlGeneratorMock.Setup(x => x.GenerateXmlAsync(doc, It.IsAny<CancellationToken>()))
            .ReturnsAsync("<xml/>");

        var result = await _strategy.BuildXmlAsync(doc);

        result.Xml.Should().Be("<xml/>");
        result.AccessKey.Value.Should().HaveLength(49);
        _xmlGeneratorMock.Verify(x => x.GenerateXmlAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildRidePdfAsync_ShouldDelegateToRideGenerator()
    {
        var doc = CreateValidFactura();
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
        _rideGeneratorMock.Setup(x => x.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pdfBytes);

        var result = await _strategy.BuildRidePdfAsync(doc);

        result.Should().BeEquivalentTo(pdfBytes);
        _rideGeneratorMock.Verify(x => x.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_WithNullXmlGenerator_ShouldThrow()
    {
        var act = () => new FacturaStrategy(null!, _rideGeneratorMock.Object, _sriOptions);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullRideGenerator_ShouldThrow()
    {
        var act = () => new FacturaStrategy(_xmlGeneratorMock.Object, null!, _sriOptions);

        act.Should().Throw<ArgumentNullException>();
    }
}
