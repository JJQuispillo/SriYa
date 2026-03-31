using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class ComprobanteRetencionXmlBuilderTests
{
    private readonly ComprobanteRetencionXmlBuilder _builder = new();

    private static Document CreateTestComprobanteRetencion()
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
            ["periodoFiscal"] = "01/2024"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "PROVEEDOR RETENIDO S.A.",
            ["identificacion"] = "1790054321001"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);

        // AuxiliaryCode format: "codDocSustento|numDocSustento|fechaEmisionDocSustento|numAutDocSustento"
        var item = DocumentItem.Create(
            document.Id,
            mainCode: "01",
            description: "Retencion Fuente servicios",
            quantity: 1m,
            unitPrice: 500.00m,
            discount: 0m,
            taxRate: 1.75m,
            taxCode: "1",
            taxPercentageCode: "303",
            auxiliaryCode: "01|001-001-000000099|10/01/2024|2401202401179226807100110010010000000991234567890");

        document.AddItem(item);
        return document;
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldProduceValidXml()
    {
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("comprobanteRetencion");
    }

    [Fact]
    public async Task GenerateXmlAsync_RootElementVersion_Is2_0_0()
    {
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("2.0.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoTributariaCodDoc_Is07()
    {
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("codDoc")!.Value.Should().Be("07");
    }

    [Fact]
    public async Task GenerateXmlAsync_Impuesto_HasCorrectFields()
    {
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var impuestos = xDoc.Root!.Element("impuestos");
        impuestos.Should().NotBeNull();

        var impuesto = impuestos!.Elements("impuesto").ToList();
        impuesto.Should().HaveCount(1);

        impuesto[0].Element("codigo")!.Value.Should().Be("1");
        impuesto[0].Element("codigoPorcentaje")!.Value.Should().Be("303");
        impuesto[0].Element("tarifa")!.Value.Should().Be("1.75");
        impuesto[0].Element("baseImponible")!.Value.Should().Be("500.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_valorRetenido_IsCalculatedCorrectly()
    {
        // baseImponible = 1 * 500.00 = 500.00
        // valorRetenido = 500.00 * 1.75 / 100 = 8.75
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var impuesto = xDoc.Root!.Element("impuestos")!.Element("impuesto")!;
        impuesto.Element("valorRetenido")!.Value.Should().Be("8.75");
    }

    [Fact]
    public async Task GenerateXmlAsync_SustentoFields_AreMappedCorrectly()
    {
        // Sustento fields are now domain properties on DocumentItem (not parsed from AuxiliaryCode).
        // codDocSustento = SustentoDocumentType; falls back to MainCode when null.
        // The test document uses SustentoDocumentType = null so codDocSustento falls back to MainCode = "01".
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var impuesto = xDoc.Root!.Element("impuestos")!.Element("impuesto")!;
        // SustentoDocumentType is null → fallback to MainCode "01"
        impuesto.Element("codDocSustento")!.Value.Should().Be("01");
        // Other sustento fields are null/empty since not set on this item
        impuesto.Element("numDocSustento")!.Value.Should().BeEmpty();
        impuesto.Element("fechaEmisionDocSustento")!.Value.Should().BeEmpty();
        impuesto.Element("numAutDocSustento")!.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateXmlAsync_infoCompRetencion_HasPeriodoFiscal()
    {
        var document = CreateTestComprobanteRetencion();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoComp = xDoc.Root!.Element("infoCompRetencion");
        infoComp.Should().NotBeNull();
        infoComp!.Element("periodoFiscal")!.Value.Should().Be("01/2024");
        infoComp.Element("razonSocialSujetoRetenido")!.Value.Should().Be("PROVEEDOR RETENIDO S.A.");
        infoComp.Element("identificacionSujetoRetenido")!.Value.Should().Be("1790054321001");
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
            .WithMessage("*ComprobanteRetencion*");
    }

    // ─── Batch 6: sustento domain-field tests ──────────────────────────────────

    private static Document CreateComprobanteRetencionWithSustentoFields(
        string sustentoDocumentType = "01",
        string sustentoDocumentNumber = "001-001-000000099",
        DateTime? sustentoDocumentIssueDate = null,
        string sustentoDocumentAuthNumber = "2401202401179226807100110010010000000991234567890")
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
            ["periodoFiscal"] = "01/2024"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "PROVEEDOR RETENIDO S.A.",
            ["identificacion"] = "1790054321001"
        };

        var issueDate = sustentoDocumentIssueDate ?? new DateTime(2024, 1, 10);

        var document = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "303",
            description: "Retencion Fuente servicios",
            quantity: 1m,
            unitPrice: 500.00m,
            discount: 0m,
            taxRate: 1.75m,
            taxCode: "1",
            taxPercentageCode: "303",
            auxiliaryCode: null,
            sustentoDocumentType: sustentoDocumentType,
            sustentoDocumentNumber: sustentoDocumentNumber,
            sustentoDocumentIssueDate: issueDate,
            sustentoDocumentAuthNumber: sustentoDocumentAuthNumber);

        document.AddItem(item);
        return document;
    }

    [Fact]
    public async Task BuildXml_WithSustentoFields_GeneratesCorrectImpuestoXml()
    {
        // Arrange
        var issueDate = new DateTime(2024, 3, 15);
        var document = CreateComprobanteRetencionWithSustentoFields(
            sustentoDocumentType: "04",
            sustentoDocumentNumber: "002-001-000000042",
            sustentoDocumentIssueDate: issueDate,
            sustentoDocumentAuthNumber: "49digitauthcode0000000000000000000000000000000000");

        // Act
        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);
        var impuesto = xDoc.Root!.Element("impuestos")!.Element("impuesto")!;

        // Assert
        impuesto.Element("codDocSustento")!.Value.Should().Be("04");
        impuesto.Element("numDocSustento")!.Value.Should().Be("002-001-000000042");
        impuesto.Element("fechaEmisionDocSustento")!.Value.Should().Be("15/03/2024");
        impuesto.Element("numAutDocSustento")!.Value.Should().Be("49digitauthcode0000000000000000000000000000000000");
    }

    [Fact]
    public async Task BuildXml_SustentoIssueDateFormattedAsDdMmYyyy()
    {
        // Arrange: date with day=5, month=7, year=2025 — expect "05/07/2025"
        var specificDate = new DateTime(2025, 7, 5);
        var document = CreateComprobanteRetencionWithSustentoFields(
            sustentoDocumentIssueDate: specificDate);

        // Act
        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);
        var impuesto = xDoc.Root!.Element("impuestos")!.Element("impuesto")!;

        // Assert: dd/MM/yyyy with zero-padding
        impuesto.Element("fechaEmisionDocSustento")!.Value.Should().Be("05/07/2025");
    }

    [Fact]
    public async Task BuildXml_WhenSustentoDocumentTypeNull_FallsBackToMainCode()
    {
        // The strategy throws before reaching the builder when SustentoDocumentType is null.
        // At the builder level (bypassing the strategy), null SustentoDocumentType falls back to MainCode.
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
            ["periodoFiscal"] = "01/2024"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "PROVEEDOR RETENIDO S.A.",
            ["identificacion"] = "1790054321001"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.ComprobanteRetencion, issuerInfo, buyerInfo);

        // Item with SustentoDocumentType = null; builder falls back to MainCode = "07"
        var item = DocumentItem.Create(
            document.Id,
            mainCode: "07",
            description: "Retencion con fallback",
            quantity: 1m,
            unitPrice: 200.00m,
            discount: 0m,
            taxRate: 2m,
            taxCode: "1",
            taxPercentageCode: "304",
            auxiliaryCode: null,
            sustentoDocumentType: null);  // explicit null — builder uses MainCode as fallback

        document.AddItem(item);

        // Act — call builder directly (not via strategy, which would throw)
        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);
        var impuesto = xDoc.Root!.Element("impuestos")!.Element("impuesto")!;

        // Assert: codDocSustento should equal MainCode "07" due to fallback
        impuesto.Element("codDocSustento")!.Value.Should().Be("07");
    }
}
