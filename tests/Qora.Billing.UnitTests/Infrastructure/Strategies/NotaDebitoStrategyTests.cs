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

public class NotaDebitoStrategyTests
{
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly NotaDebitoStrategy _strategy;

    public NotaDebitoStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new NotaDebitoStrategy(new NotaDebitoXmlBuilder(), _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidNotaDebito(decimal taxRate = 15m)
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000001",
            ["fechaEmisionDocSustento"] = "01/03/2026"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CLIENTE TEST",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.NotaDebito, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "M001", "Motivo", 1, 10m, 0, taxRate, "2", "4"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeNotaDebito()
    {
        _strategy.DocumentType.Should().Be(DocumentType.NotaDebito);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidNotaDebito();

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

        errors.Should().Contain(e => e.Contains("NotaDebito") && e.Contains("Factura"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithEmptyItems_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["codDocSustento"] = "01", ["numDocSustento"] = "001-001-000000001",
            ["fechaEmisionDocSustento"] = "01/03/2026"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.NotaDebito, issuerInfo,
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
        var doc = CreateValidNotaDebito(taxRate: 8m); // 8% is not a valid SRI rate

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("invalid IVA rate") && e.Contains("8"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidIvaRates_ShouldReturnNoErrors()
    {
        // 0%, 5%, 12%, 15% are all valid
        var doc0 = CreateValidNotaDebito(taxRate: 0m);
        var errors0 = await _strategy.ValidateDocumentAsync(doc0);
        errors0.Should().BeEmpty();

        var doc5 = CreateValidNotaDebito(taxRate: 5m);
        var errors5 = await _strategy.ValidateDocumentAsync(doc5);
        errors5.Should().BeEmpty();

        var doc12 = CreateValidNotaDebito(taxRate: 12m);
        var errors12 = await _strategy.ValidateDocumentAsync(doc12);
        errors12.Should().BeEmpty();

        var doc15 = CreateValidNotaDebito(taxRate: 15m);
        var errors15 = await _strategy.ValidateDocumentAsync(doc15);
        errors15.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingCodDocSustento_ShouldReturnError()
    {
        var doc = CreateValidNotaDebito();
        doc.IssuerInfo.Remove("codDocSustento");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("codDocSustento"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidCodDocSustento_ShouldReturnError()
    {
        var doc = CreateValidNotaDebito();
        doc.IssuerInfo["codDocSustento"] = "04"; // not "01"

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("codDocSustento") && e.Contains("01"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidNumDocSustentoFormat_ShouldReturnError()
    {
        var doc = CreateValidNotaDebito();
        doc.IssuerInfo["numDocSustento"] = "001-001-00000001"; // 8 digits in last group

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("numDocSustento") && e.Contains("format"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingFechaEmisionDocSustento_ShouldReturnError()
    {
        var doc = CreateValidNotaDebito();
        doc.IssuerInfo.Remove("fechaEmisionDocSustento");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("fechaEmisionDocSustento"));
    }
}
