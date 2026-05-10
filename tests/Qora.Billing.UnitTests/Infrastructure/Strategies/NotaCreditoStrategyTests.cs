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

public class NotaCreditoStrategyTests
{
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly NotaCreditoStrategy _strategy;

    public NotaCreditoStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new NotaCreditoStrategy(new NotaCreditoXmlBuilder(), _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidNotaCredito(decimal taxRate = 15m)
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["codDocModificado"] = "01",
            ["numDocModificado"] = "001-001-000000001",
            ["fechaEmisionDocSustento"] = "01/03/2026",
            ["razonModificacion"] = "Anulación de factura"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CLIENTE TEST",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.NotaCredito, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Item", 1, 10m, 0, taxRate, "2", "4"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeNotaCredito()
    {
        _strategy.DocumentType.Should().Be(DocumentType.NotaCredito);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidNotaCredito();

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

        errors.Should().Contain(e => e.Contains("NotaCredito") && e.Contains("Factura"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithEmptyItems_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["codDocModificado"] = "01", ["numDocModificado"] = "001-001-000000001",
            ["fechaEmisionDocSustento"] = "01/03/2026", ["razonModificacion"] = "Anulación"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.NotaCredito, issuerInfo,
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
        var doc = CreateValidNotaCredito(taxRate: 10m);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("invalid IVA rate") && e.Contains("10"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidIvaRates_ShouldReturnNoErrors()
    {
        // 0%, 5%, 12%, 15% are all valid
        var doc0 = CreateValidNotaCredito(taxRate: 0m);
        var errors0 = await _strategy.ValidateDocumentAsync(doc0);
        errors0.Should().BeEmpty();

        var doc5 = CreateValidNotaCredito(taxRate: 5m);
        var errors5 = await _strategy.ValidateDocumentAsync(doc5);
        errors5.Should().BeEmpty();

        var doc12 = CreateValidNotaCredito(taxRate: 12m);
        var errors12 = await _strategy.ValidateDocumentAsync(doc12);
        errors12.Should().BeEmpty();

        var doc15 = CreateValidNotaCredito(taxRate: 15m);
        var errors15 = await _strategy.ValidateDocumentAsync(doc15);
        errors15.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingCodDocModificado_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo.Remove("codDocModificado");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("codDocModificado"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidCodDocModificado_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo["codDocModificado"] = "04"; // not "01"

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("codDocModificado") && e.Contains("04"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingNumDocModificado_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo.Remove("numDocModificado");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("numDocModificado"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidNumDocModificadoFormat_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo["numDocModificado"] = "001-001-00000001"; // 8 digits in last group, not 9

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("numDocModificado") && e.Contains("invalid format"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingFechaEmisionDocSustento_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo.Remove("fechaEmisionDocSustento");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("fechaEmisionDocSustento"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingRazonModificacion_ShouldReturnError()
    {
        var doc = CreateValidNotaCredito();
        doc.IssuerInfo.Remove("razonModificacion");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("razonModificacion"));
    }
}
