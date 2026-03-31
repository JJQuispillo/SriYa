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

public class GuiaRemisionStrategyTests
{
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly GuiaRemisionStrategy _strategy;
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();

    public GuiaRemisionStrategyTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });
        _strategy = new GuiaRemisionStrategy(new GuiaRemisionXmlBuilder(), _rideGeneratorMock.Object, _sriOptions);
    }

    private static Document CreateValidGuiaRemision()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["razonSocialTransportista"] = "TRANSPORTISTA S.A.",
            ["rucTransportista"] = "1792268071001",
            ["obligadoContabilidad"] = "SI",
            ["fechaInicioTransporte"] = "01/03/2026",
            ["fechaFinTransporte"] = "05/03/2026",
            ["placa"] = "ABC-1234"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["identificacionDestinatario"] = "1234567890001",
            ["razonSocialDestinatario"] = "DESTINATARIO TEST",
            ["dirDestinatario"] = "Dirección destino",
            ["motivoTraslado"] = "Venta de mercadería",
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000001",
            ["numAutDocSustento"] = "1234567890123456789012345678901234567890123456789",
            ["fechaEmisionDocSustento"] = "01/03/2026"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.GuiaRemision, issuerInfo, buyerInfo);
        doc.AddItem(DocumentItem.Create(doc.Id, "P001", "Producto", 1, 10m, 0, 0m, "2", "0"));
        return doc;
    }

    [Fact]
    public void DocumentType_ShouldBeGuiaRemision()
    {
        _strategy.DocumentType.Should().Be(DocumentType.GuiaRemision);
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithValidDocument_ShouldReturnNoErrors()
    {
        var doc = CreateValidGuiaRemision();

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

        errors.Should().Contain(e => e.Contains("GuiaRemision") && e.Contains("Factura"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithEmptyItems_ShouldReturnError()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ruc"] = "1792268071001", ["razonSocial"] = "TEST",
            ["dirMatriz"] = "Quito", ["estab"] = "001",
            ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["razonSocialTransportista"] = "TRANS", ["rucTransportista"] = "1792268071001",
            ["obligadoContabilidad"] = "SI", ["fechaInicioTransporte"] = "01/03/2026",
            ["fechaFinTransporte"] = "05/03/2026", ["placa"] = "ABC-1234"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.GuiaRemision, issuerInfo,
            new Dictionary<string, string>
            {
                ["identificacionDestinatario"] = "1234567890001",
                ["razonSocialDestinatario"] = "DEST", ["dirDestinatario"] = "Dir",
                ["motivoTraslado"] = "Venta", ["codDocSustento"] = "01",
                ["numDocSustento"] = "001-001-000000001",
                ["numAutDocSustento"] = "1234567890123456789012345678901234567890123456789",
                ["fechaEmisionDocSustento"] = "01/03/2026"
            });

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("at least one"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_NoIvaValidation_ValidDocumentWithNoTax_ShouldReturnNoErrors()
    {
        // GuiaRemision does not validate IVA — items with taxRate=0 should not produce errors
        var doc = CreateValidGuiaRemision();
        // Item already has taxRate=0 in the factory, no errors expected
        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingRucTransportista_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.IssuerInfo.Remove("rucTransportista");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("rucTransportista"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidRucTransportista_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.IssuerInfo["rucTransportista"] = "12345"; // not 13 digits

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("rucTransportista") && e.Contains("13"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingPlaca_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.IssuerInfo.Remove("placa");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("placa"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithInvalidFechaInicioTransporteFormat_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.IssuerInfo["fechaInicioTransporte"] = "2026-03-01"; // wrong format (ISO instead of dd/MM/yyyy)

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("fechaInicioTransporte") && e.Contains("format"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithFechaFinBeforeFechaInicio_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.IssuerInfo["fechaInicioTransporte"] = "10/03/2026";
        doc.IssuerInfo["fechaFinTransporte"] = "01/03/2026"; // before inicio

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("fechaFinTransporte") && e.Contains("fechaInicioTransporte"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithMissingMotivoTraslado_ShouldReturnError()
    {
        var doc = CreateValidGuiaRemision();
        doc.BuyerInfo.Remove("motivoTraslado");

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("motivoTraslado"));
    }

    // ── Multi-destinatario tests (T-015) ──────────────────────────────────────

    private static Dictionary<string, string> CreateValidMultiDestinatarioIssuerInfo() =>
        new()
        {
            ["ruc"] = "1792268071001",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["dirMatriz"] = "Quito",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["razonSocialTransportista"] = "TRANSPORTISTA S.A.",
            ["rucTransportista"] = "1792268071001",
            ["obligadoContabilidad"] = "SI",
            ["fechaInicioTransporte"] = "01/03/2026",
            ["fechaFinTransporte"] = "05/03/2026",
            ["placa"] = "ABC-1234"
        };

    private static Dictionary<string, string> CreateSustentoDocBuyerInfo() =>
        new()
        {
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000001"
        };

    private static Destinatario CreateValidDestinatario(
        string identificacion = "1712345678001",
        string razonSocial = "DESTINATARIO TEST S.A.",
        bool addItems = true)
    {
        var dest = Destinatario.Create(
            identificacion,
            razonSocial,
            "Av. Principal 123",
            "Venta de mercaderia",
            rucTransportista: "1792268071001");

        if (addItems)
            dest.AddItem(DestinatarioItem.Create("ITEM001", "Producto A", 5m));

        return dest;
    }

    [Fact]
    public async Task ValidateDocumentAsync_WithTwoDestinatariosWithItems_ShouldReturnNoErrors()
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        doc.AddDestinatario(CreateValidDestinatario("1712345678001", "DESTINATARIO UNO"));
        doc.AddDestinatario(CreateValidDestinatario("0912345678001", "DESTINATARIO DOS"));

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateDocumentAsync_EmptyDestinatariosAndEmptyBuyerInfo_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            new Dictionary<string, string>()); // no BuyerInfo

        // No Destinatarios entities and no BuyerInfo

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.ToLower().Contains("at least one") || e.ToLower().Contains("destinatario"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_DestinatarioWithMissingIdentificacion_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        // Create destinatario with empty identificacion
        var dest = Destinatario.Create(
            string.Empty, // missing
            "DEST S.A.",
            "Dir",
            "Motivo",
            "1792268071001");
        dest.AddItem(DestinatarioItem.Create("ITEM001", "Producto", 1m));
        doc.AddDestinatario(dest);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("IdentificacionDestinatario"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_DestinatarioWithMissingRazonSocial_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        // Create destinatario with empty razonSocial
        var dest = Destinatario.Create(
            "1712345678001",
            string.Empty, // missing
            "Dir",
            "Motivo",
            "1792268071001");
        dest.AddItem(DestinatarioItem.Create("ITEM001", "Producto", 1m));
        doc.AddDestinatario(dest);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("RazonSocialDestinatario"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_DestinatarioWithNoItems_ShouldReturnError()
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        // Destinatario with no items
        var dest = CreateValidDestinatario(addItems: false);
        doc.AddDestinatario(dest);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("at least one detalle") || e.Contains("detalle item"));
    }

    [Fact]
    public void ValidateDocumentAsync_DestinatarioItemWithZeroCantidad_ShouldThrowAtCreation()
    {
        // DestinatarioItem.Create enforces cantidad > 0 — entity-level guard
        var act = () => DestinatarioItem.Create("ITEM001", "Producto", 0m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*CantidadDetalle*");
    }

    [Fact]
    public void ValidateDocumentAsync_DestinatarioItemWithNegativeCantidad_ShouldThrowAtCreation()
    {
        // Strategy also validates cantidad > 0; entity guard fires first
        var act = () => DestinatarioItem.Create("ITEM001", "Producto", -1m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*CantidadDetalle*");
    }

    [Fact]
    public async Task ValidateDocumentAsync_DestinatarioItemWithEmptyDescripcion_ShouldReturnError()
    {
        // DestinatarioItem.Create allows empty descripcion (no guard there),
        // so the strategy must catch it during validation.
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateValidMultiDestinatarioIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        var dest = Destinatario.Create("1712345678001", "DEST S.A.", "Dir", "Motivo", "1792268071001");

        // Use reflection to bypass the item creation guard and create an item with empty description
        // so we can test the strategy-level validation path.
        // Alternative: test via the strategy's validation logic by checking item after creation.
        // Since DestinatarioItem.Create does NOT enforce non-empty DescripcionDetalle (only cantidad > 0),
        // we can pass an empty string to exercise the strategy path.
        dest.AddItem(DestinatarioItem.Create("ITEM001", "", 5m));
        doc.AddDestinatario(dest);

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().Contain(e => e.Contains("DescripcionDetalle"));
    }

    [Fact]
    public async Task ValidateDocumentAsync_LegacySingleDestinatarioViaBuyerInfo_ShouldPassValidation()
    {
        // Legacy path: no Destinatario entities, full destinatario in BuyerInfo + Document.Items
        var doc = CreateValidGuiaRemision();

        // Confirm legacy path (no entities)
        doc.Destinatarios.Should().BeEmpty();

        var errors = await _strategy.ValidateDocumentAsync(doc);

        errors.Should().BeEmpty();
    }
}
