using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Strategies;

public class ComprobanteRetencionStrategyTests
{
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly ComprobanteRetencionStrategy _strategy;
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();

    public ComprobanteRetencionStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new ComprobanteRetencionStrategy(new ComprobanteRetencionXmlBuilder(), _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidComprobanteRetencion()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["periodoFiscal"] = "03/2026"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "SUJETO RETENIDO TEST",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);
        // TaxCode "1"=Renta, TaxRate=5% (retention percentage)
        doc.AddItem(DocumentItem.Create(doc.Id, "303", "Honorarios", 1, 100m, 0, 10m, "1", "10"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeComprobanteRetencion()
    {
        _strategy.DocumentType.Should().Be(DocumentType.ComprobanteRetencion);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidComprobanteRetencion();

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithWrongDocumentType_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.Factura,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("ComprobanteRetencion") && e.Contains("Factura"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithEmptyItems_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["periodoFiscal"] = "03/2026"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo,
            new Dictionary<string, string>
            {
                ["tipoIdentificacion"] = "04", ["razonSocial"] = "SUJETO",
                ["identificacion"] = "9999999999999"
            });

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("at least one"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingPeriodoFiscal_ShouldReturnError()
    {
        var doc = CreateValidComprobanteRetencion();
        doc.IssuerInfo.Remove("periodoFiscal");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("periodoFiscal"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidPeriodoFiscalFormat_ShouldReturnError()
    {
        var doc = CreateValidComprobanteRetencion();
        doc.IssuerInfo["periodoFiscal"] = "2026/03"; // wrong format (YYYY/MM instead of MM/YYYY)

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("periodoFiscal") && e.Contains("MM/YYYY"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidTaxCode_ShouldReturnError()
    {
        var doc = CreateValidComprobanteRetencion();
        // Replace the item with one that has an invalid TaxCode "3"
        var issuerInfo = doc.IssuerInfo;
        var buyerInfo = doc.BuyerInfo;
        var newDoc = Document.Create(doc.TenantId, DocumentType.ComprobanteRetencion,
            new Dictionary<string, string>(issuerInfo),
            new Dictionary<string, string>(buyerInfo));
        newDoc.AddItem(DocumentItem.Create(newDoc.Id, "303", "Honorarios", 1, 100m, 0, 10m, "3", "10")); // "3" invalid

        var errors = await _strategy.ValidateDocumentAsync(newDoc);

        errors.Should().Contain(e => e.Contains("tax type code") && e.Contains("3"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithZeroTaxRate_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["periodoFiscal"] = "03/2026"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo,
            new Dictionary<string, string>
            {
                ["tipoIdentificacion"] = "04", ["razonSocial"] = "SUJETO",
                ["identificacion"] = "9999999999999"
            });
        // TaxRate = 0 — invalid, must be > 0
        doc.AddItem(DocumentItem.Create(doc.Id, "303", "Honorarios", 1, 100m, 0, 0m, "1", "0"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("retention rate") && e.Contains("0"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithAllThreeValidTaxCodes_ShouldReturnNoErrors()
    {
        // "1"=Renta, "2"=IVA, "6"=ISD — all valid
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["periodoFiscal"] = "03/2026"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "SUJETO RETENIDO TEST",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "303", "Honorarios profesionales", 1, 100m, 0, 10m, "1", "10"));
        doc.AddItem(DocumentItem.Create(doc.Id, "721", "Retención IVA 30%", 1, 100m, 0, 30m, "2", "9"));
        doc.AddItem(DocumentItem.Create(doc.Id, "4580", "ISD transferencia", 1, 100m, 0, 5m, "6", "5"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    // ─── Batch 6: BuildXmlAsync sustento guard tests ────────────────────────────

    [Fact]
    public async Task BuildXmlAsync_WhenSustentoDocumentTypeNull_ThrowsInvalidOperationException()
    {
        // Arrange: document with an item where SustentoDocumentType is null
        var doc = CreateValidComprobanteRetencion();

        // Rebuild the doc with an item that has no SustentoDocumentType
        var issuerInfo = new Dictionary<string, string>(doc.IssuerInfo);
        var buyerInfo = new Dictionary<string, string>(doc.BuyerInfo);
        var docWithoutSustento = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);

        // SustentoDocumentType intentionally left null
        docWithoutSustento.AddItem(DocumentItem.Create(
            docWithoutSustento.Id,
            mainCode: "303",
            description: "Honorarios sin sustento",
            quantity: 1,
            unitPrice: 100m,
            discount: 0,
            taxRate: 10m,
            taxCode: "1",
            taxPercentageCode: "303",
            auxiliaryCode: null,
            sustentoDocumentType: null));  // missing — strategy must throw

        // Act
        var act = async () => await _strategy.BuildXmlAsync(docWithoutSustento);

        // Assert: strategy guard throws before reaching the XML builder
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SustentoDocumentType*");
    }
}
