using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class GuiaRemisionXmlBuilderTests
{
    private readonly GuiaRemisionXmlBuilder _builder = new();

    private static Document CreateTestGuiaRemision()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ambiente"] = "1",
            ["tipoEmision"] = "1",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["ruc"] = "1792268071001",
            ["claveAcceso"] = "1234567890123456789012345678901234567890123456789",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["dirMatriz"] = "Quito, Av. Amazonas",
            ["fechaEmision"] = "15/01/2024",
            ["razonSocialTransportista"] = "TRANSPORTES EXPRESS S.A.",
            ["rucTransportista"] = "1790012345001",
            ["obligadoContabilidad"] = "SI",
            ["fechaInicioTransporte"] = "15/01/2024",
            ["fechaFinTransporte"] = "16/01/2024",
            ["placa"] = "ABC-1234"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["identificacionDestinatario"] = "1712345678",
            ["razonSocialDestinatario"] = "DESTINATARIO S.A.",
            ["dirDestinatario"] = "Guayaquil, Av. Principal 456",
            ["motivoTraslado"] = "Venta de mercaderia",
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000020",
            ["numAutDocSustento"] = "2401202401179226807100110010010000000201234567890",
            ["fechaEmisionDocSustento"] = "14/01/2024"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.GuiaRemision, issuerInfo, buyerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "PROD001",
            description: "Cajas de producto",
            quantity: 10m,
            unitPrice: 0m,   // GuiaRemision has no pricing
            discount: 0m,
            taxRate: 0m,
            taxCode: "2",
            taxPercentageCode: "0");

        document.AddItem(item);
        return document;
    }

    /// <summary>
    /// Creates a minimal valid issuerInfo dictionary for multi-destinatario tests.
    /// Includes all system-generated fields that the builder requires.
    /// </summary>
    private static Dictionary<string, string> CreateBaseIssuerInfo() =>
        new()
        {
            ["ambiente"] = "1",
            ["tipoEmision"] = "1",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["ruc"] = "1792268071001",
            ["claveAcceso"] = "1234567890123456789012345678901234567890123456789",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
            ["dirMatriz"] = "Quito, Av. Amazonas",
            ["fechaEmision"] = "15/01/2024",
            ["razonSocialTransportista"] = "TRANSPORTES EXPRESS S.A.",
            ["rucTransportista"] = "1790012345001",
            ["obligadoContabilidad"] = "SI",
            ["fechaInicioTransporte"] = "15/01/2024",
            ["fechaFinTransporte"] = "16/01/2024",
            ["placa"] = "ABC-1234"
        };

    /// <summary>
    /// Creates a minimal valid BuyerInfo for the multi-destinatario path (only sustento doc fields).
    /// </summary>
    private static Dictionary<string, string> CreateSustentoDocBuyerInfo() =>
        new()
        {
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000020"
        };

    private static Destinatario CreateDestinatario(
        string identificacion = "1712345678001",
        string razonSocial = "DESTINATARIO TEST S.A.",
        string direccion = "Guayaquil, Av. Principal 456",
        string motivoTraslado = "Venta de mercaderia",
        string? rutaEntrega = null)
    {
        var dest = Destinatario.Create(
            identificacion,
            razonSocial,
            direccion,
            motivoTraslado,
            rucTransportista: "1790012345001",
            rutaEntrega: rutaEntrega);

        dest.AddItem(DestinatarioItem.Create("ITEM001", "Producto de prueba", 5m));
        return dest;
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldProduceValidXml()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("guiaRemision");
    }

    [Fact]
    public async Task GenerateXmlAsync_RootElementVersion_Is1_1_0()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("1.1.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoTributariaCodDoc_Is06()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("codDoc")!.Value.Should().Be("06");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoGuiaRemision_HasTransporterFields()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoGuia = xDoc.Root!.Element("infoGuiaRemision");
        infoGuia.Should().NotBeNull();
        infoGuia!.Element("razonSocialTransportista")!.Value.Should().Be("TRANSPORTES EXPRESS S.A.");
        infoGuia.Element("tipoIdentificacionTransportista")!.Value.Should().Be("04");
        infoGuia.Element("rucTransportista")!.Value.Should().Be("1790012345001");
        infoGuia.Element("placa")!.Value.Should().Be("ABC-1234");
        infoGuia.Element("fechaIniTransporte")!.Value.Should().Be("15/01/2024");
        infoGuia.Element("fechaFinTransporte")!.Value.Should().Be("16/01/2024");
    }

    [Fact]
    public async Task GenerateXmlAsync_Destinatario_HasRequiredFields()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var destinatarios = xDoc.Root!.Element("destinatarios");
        destinatarios.Should().NotBeNull();

        var destinatario = destinatarios!.Element("destinatario");
        destinatario.Should().NotBeNull();
        destinatario!.Element("identificacionDestinatario")!.Value.Should().Be("1712345678");
        destinatario.Element("razonSocialDestinatario")!.Value.Should().Be("DESTINATARIO S.A.");
        destinatario.Element("dirDestinatario")!.Value.Should().Be("Guayaquil, Av. Principal 456");
        destinatario.Element("motivoTraslado")!.Value.Should().Be("Venta de mercaderia");
        destinatario.Element("codDocSustento")!.Value.Should().Be("01");
        destinatario.Element("numDocSustento")!.Value.Should().Be("001-001-000000020");
        destinatario.Element("fechaEmisionDocSustento")!.Value.Should().Be("14/01/2024");
    }

    [Fact]
    public async Task GenerateXmlAsync_NoImpuestosElement()
    {
        var document = CreateTestGuiaRemision();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        // GuiaRemision is a transport document — no tax/IVA elements at root level
        xDoc.Root!.Element("impuestos").Should().BeNull();
        xDoc.Root.Element("totalConImpuestos").Should().BeNull();
    }

    [Fact]
    public async Task GenerateXmlAsync_WithWrongDocumentType_ShouldThrow()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DocumentType.Factura,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var act = () => _builder.GenerateXmlAsync(document);

        await act.Should().ThrowAsync<Qora.Billing.Domain.Exceptions.DocumentValidationException>()
            .WithMessage("*GuiaRemision*");
    }

    // ── Multi-destinatario tests (T-014) ──────────────────────────────────────

    [Fact]
    public async Task GenerateXmlAsync_SingleDestinatarioViaEntity_ProducesCorrectXml()
    {
        // Single destinatario using the new Document.Destinatarios path
        var document = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateBaseIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        var dest = CreateDestinatario(
            identificacion: "1712345678001",
            razonSocial: "DESTINATARIO UNICO S.A.",
            direccion: "Cuenca, Calle 10");
        document.AddDestinatario(dest);

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var destinatariosEl = xDoc.Root!.Element("destinatarios");
        destinatariosEl.Should().NotBeNull("root <destinatarios> element must exist");

        var destinatariosCount = destinatariosEl!.Elements("destinatario").Count();
        destinatariosCount.Should().Be(1);

        var destinatarioEl = destinatariosEl.Element("destinatario")!;
        destinatarioEl.Element("identificacionDestinatario")!.Value.Should().Be("1712345678001");
        destinatarioEl.Element("razonSocialDestinatario")!.Value.Should().Be("DESTINATARIO UNICO S.A.");
        destinatarioEl.Element("dirDestinatario")!.Value.Should().Be("Cuenca, Calle 10");

        var detalles = destinatarioEl.Element("detalles");
        detalles.Should().NotBeNull("each destinatario must contain a <detalles> block");
        detalles!.Elements("detalle").Should().HaveCount(1);
        detalles.Element("detalle")!.Element("descripcionDetalle")!.Value.Should().Be("Producto de prueba");
    }

    [Fact]
    public async Task GenerateXmlAsync_MultipleDestinatarios_ProducesMultipleBlocks()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateBaseIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        // Destinatario 1 — two items
        var dest1 = Destinatario.Create("1712345678001", "DESTINATARIO UNO S.A.", "Quito, Norte", "Venta", "1790012345001");
        dest1.AddItem(DestinatarioItem.Create("A001", "Producto Alfa", 3m));
        dest1.AddItem(DestinatarioItem.Create("A002", "Producto Beta", 7m));
        document.AddDestinatario(dest1);

        // Destinatario 2 — one item
        var dest2 = Destinatario.Create("0912345678001", "DESTINATARIO DOS S.A.", "Guayaquil, Centro", "Traslado interno", "1790012345001");
        dest2.AddItem(DestinatarioItem.Create("B001", "Producto Gamma", 10m));
        document.AddDestinatario(dest2);

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var destinatariosEl = xDoc.Root!.Element("destinatarios")!;
        var destElements = destinatariosEl.Elements("destinatario").ToList();
        destElements.Should().HaveCount(2, "two destinatario entities must produce two <destinatario> blocks");

        // Verify first destinatario has 2 items (no cross-contamination)
        var detalles1 = destElements[0].Element("detalles")!.Elements("detalle").ToList();
        detalles1.Should().HaveCount(2);
        detalles1.Select(d => d.Element("codigoInterno")!.Value)
            .Should().BeEquivalentTo(["A001", "A002"]);

        // Verify second destinatario has only 1 item (no cross-contamination)
        var detalles2 = destElements[1].Element("detalles")!.Elements("detalle").ToList();
        detalles2.Should().HaveCount(1);
        detalles2[0].Element("codigoInterno")!.Value.Should().Be("B001");
    }

    [Fact]
    public async Task GenerateXmlAsync_OptionalDestinatarioFields_OmittedWhenNull()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DocumentType.GuiaRemision,
            CreateBaseIssuerInfo(),
            CreateSustentoDocBuyerInfo());

        // Create destinatario with no optional fields (no ruta, etc.)
        var dest = Destinatario.Create(
            "1712345678001", "DEST S.A.", "Cuenca", "Venta", "1790012345001",
            rutaEntrega: null,
            docAduaneroUnico: null,
            codEstabDestino: null);
        dest.AddItem(DestinatarioItem.Create("C001", "Producto", 1m));
        document.AddDestinatario(dest);

        // Also ensure BuyerInfo has no optional sustento doc fields
        // (numAutDocSustento and fechaEmisionDocSustento are optional in multi-destinatario path)

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var destinatarioEl = xDoc.Root!.Element("destinatarios")!.Element("destinatario")!;

        // Optional fields must be absent from XML when null
        destinatarioEl.Element("ruta").Should().BeNull("ruta must be omitted when not set");
        destinatarioEl.Element("docAduaneroUnico").Should().BeNull("docAduaneroUnico must be omitted when not set");
        destinatarioEl.Element("codEstabDestino").Should().BeNull("codEstabDestino must be omitted when not set");
        destinatarioEl.Element("numAutDocSustento").Should().BeNull("numAutDocSustento must be omitted when absent in BuyerInfo");
        destinatarioEl.Element("fechaEmisionDocSustento").Should().BeNull("fechaEmisionDocSustento must be omitted when absent in BuyerInfo");
    }

    [Fact]
    public async Task GenerateXmlAsync_LegacyFallback_WithPopulatedBuyerInfo_GeneratesValidXml()
    {
        // Legacy path: no Destinatarios entities, use BuyerInfo + Document.Items
        var document = CreateTestGuiaRemision(); // uses legacy path (no AddDestinatario call)

        // Confirm it is the legacy path
        document.Destinatarios.Should().BeEmpty();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        // Must still produce a valid <destinatarios> block
        var destinatariosEl = xDoc.Root!.Element("destinatarios");
        destinatariosEl.Should().NotBeNull();
        destinatariosEl!.Elements("destinatario").Should().HaveCount(1);

        var destinatarioEl = destinatariosEl.Element("destinatario")!;
        destinatarioEl.Element("identificacionDestinatario")!.Value.Should().Be("1712345678");
        destinatarioEl.Element("razonSocialDestinatario")!.Value.Should().Be("DESTINATARIO S.A.");

        // Legacy path must include detalles built from Document.Items
        var detalles = destinatarioEl.Element("detalles");
        detalles.Should().NotBeNull();
        detalles!.Elements("detalle").Should().HaveCount(1);
    }
}
