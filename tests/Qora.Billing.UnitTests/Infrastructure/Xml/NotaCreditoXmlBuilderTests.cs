using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class NotaCreditoXmlBuilderTests
{
    private readonly NotaCreditoXmlBuilder _builder = new();

    private static Document CreateTestNotaCredito()
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
            ["codDocModificado"] = "01",
            ["numDocModificado"] = "001-001-000000010",
            ["fechaEmisionDocSustento"] = "10/01/2024",
            ["razonModificacion"] = "Devolucion de mercaderia"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CLIENTE FINAL S.A.",
            ["identificacion"] = "1790012345001",
            ["email"] = "cliente@example.com"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.NotaCredito, issuerInfo, buyerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "PROD001",
            description: "Producto devuelto",
            quantity: 2m,
            unitPrice: 10.00m,
            discount: 0m,
            taxRate: 15m,
            taxCode: "2",
            taxPercentageCode: "4");

        document.AddItem(item);
        return document;
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldProduceValidXml()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("notaCredito");
    }

    [Fact]
    public async Task GenerateXmlAsync_RootElementVersion_Is1_0_0()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("1.0.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoTributariaCodDoc_Is04()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("codDoc")!.Value.Should().Be("04");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainInfoTributaria_WithStandardFields()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("ruc")!.Value.Should().Be("1792268071001");
        infoTributaria.Element("razonSocial")!.Value.Should().Be("EMPRESA TEST S.A.");
        infoTributaria.Element("estab")!.Value.Should().Be("001");
        infoTributaria.Element("ptoEmi")!.Value.Should().Be("001");
        infoTributaria.Element("secuencial")!.Value.Should().Be("000000001");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoNotaCredito_HasRequiredDocSustentoFields()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoNotaCredito = xDoc.Root!.Element("infoNotaCredito");
        infoNotaCredito.Should().NotBeNull();
        infoNotaCredito!.Element("codDocModificado")!.Value.Should().Be("01");
        infoNotaCredito.Element("numDocModificado")!.Value.Should().Be("001-001-000000010");
        infoNotaCredito.Element("fechaEmisionDocSustento")!.Value.Should().Be("10/01/2024");
        infoNotaCredito.Element("motivo")!.Value.Should().Be("Devolucion de mercaderia");
    }

    [Fact]
    public async Task GenerateXmlAsync_valorModificacion_IsCalculatedCorrectly()
    {
        // quantity=2, unitPrice=10.00, discount=0 → subtotal=20.00, tax=20*15%=3.00 → valorModificacion=23.00
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoNotaCredito = xDoc.Root!.Element("infoNotaCredito")!;
        infoNotaCredito.Element("totalSinImpuestos")!.Value.Should().Be("20.00");
        infoNotaCredito.Element("valorModificacion")!.Value.Should().Be("23.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_Detalles_HasItemElements()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var detalles = xDoc.Root!.Element("detalles");
        detalles.Should().NotBeNull();

        var detalle = detalles!.Elements("detalle").ToList();
        detalle.Should().HaveCount(1);
        detalle[0].Element("codigoInterno")!.Value.Should().Be("PROD001");
        detalle[0].Element("descripcion")!.Value.Should().Be("Producto devuelto");
        detalle[0].Element("cantidad")!.Value.Should().Be("2.000000");
        detalle[0].Element("precioUnitario")!.Value.Should().Be("10.000000");
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
            .WithMessage("*NotaCredito*");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldHaveUtf8Encoding()
    {
        var document = CreateTestNotaCredito();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().Contain("encoding=\"utf-8\"");
    }
}
