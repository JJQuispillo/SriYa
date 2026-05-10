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

public class LiquidacionCompraStrategyTests
{
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly LiquidacionCompraStrategy _strategy;

    public LiquidacionCompraStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new LiquidacionCompraStrategy(new LiquidacionCompraXmlBuilder(), _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidLiquidacionCompra(decimal taxRate = 15m)
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
            ["tipoIdentificacionProveedor"] = "04",
            ["razonSocialProveedor"] = "PROVEEDOR TEST",
            ["identificacionProveedor"] = "9999999999999",
            ["direccionProveedor"] = "Dirección del proveedor"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.LiquidacionCompra, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Item", 1, 10m, 0, taxRate, "2", "4"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeLiquidacionCompra()
    {
        _strategy.DocumentType.Should().Be(DocumentType.LiquidacionCompra);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidLiquidacionCompra();

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

        errors.Should().Contain(e => e.Contains("LiquidacionCompra") && e.Contains("Factura"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithEmptyItems_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.LiquidacionCompra, issuerInfo,
            new Dictionary<string, string>
            {
                ["tipoIdentificacionProveedor"] = "04",
                ["razonSocialProveedor"] = "PROVEEDOR",
                ["identificacionProveedor"] = "9999999999999",
                ["direccionProveedor"] = "Dirección"
            });

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("at least one"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidIvaRate_ShouldReturnError()
    {
        var doc = CreateValidLiquidacionCompra(taxRate: 10m);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("invalid IVA rate") && e.Contains("10"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidIvaRates_ShouldReturnNoErrors()
    {
        // 0%, 5%, 12%, 15% are all valid
        var doc0 = CreateValidLiquidacionCompra(taxRate: 0m);
        var errors0 = await _strategy.ValidateDocumentAsync(doc0);
        errors0.Should().BeEmpty();

        var doc5 = CreateValidLiquidacionCompra(taxRate: 5m);
        var errors5 = await _strategy.ValidateDocumentAsync(doc5);
        errors5.Should().BeEmpty();

        var doc12 = CreateValidLiquidacionCompra(taxRate: 12m);
        var errors12 = await _strategy.ValidateDocumentAsync(doc12);
        errors12.Should().BeEmpty();

        var doc15 = CreateValidLiquidacionCompra(taxRate: 15m);
        var errors15 = await _strategy.ValidateDocumentAsync(doc15);
        errors15.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingTipoIdentificacionProveedor_ShouldReturnError()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.BuyerInfo.Remove("tipoIdentificacionProveedor");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("tipoIdentificacionProveedor"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidTipoIdentificacionProveedor_ShouldReturnError()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.BuyerInfo["tipoIdentificacionProveedor"] = "01"; // not a valid provider id type

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("tipoIdentificacionProveedor") || e.Contains("identification type"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidProviderIdType04_ShouldReturnNoErrors()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.BuyerInfo["tipoIdentificacionProveedor"] = "04"; // RUC — valid

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithPartialRetentionBlock_ShouldReturnError()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.IssuerInfo["codigoRetencion"] = "303"; // present but porcentajeRetencion and valorRetencion missing

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("porcentajeRetencion") || e.Contains("valorRetencion") || e.Contains("Retention field"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithCompleteRetentionBlock_ShouldReturnNoErrors()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.IssuerInfo["codigoRetencion"] = "303";
        doc.IssuerInfo["porcentajeRetencion"] = "30";
        doc.IssuerInfo["valorRetencion"] = "3.00";

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingIssuerRuc_ShouldReturnError()
    {
        var doc = CreateValidLiquidacionCompra();
        doc.IssuerInfo.Remove("ruc");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("ruc"));
    }
}
