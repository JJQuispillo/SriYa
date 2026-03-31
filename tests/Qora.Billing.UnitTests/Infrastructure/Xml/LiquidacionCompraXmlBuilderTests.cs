using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class LiquidacionCompraXmlBuilderTests
{
    private readonly LiquidacionCompraXmlBuilder _builder = new();

    private static Document CreateTestLiquidacionCompra(bool withRetencion = false)
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
            ["fechaEmision"] = "15/01/2024"
        };

        if (withRetencion)
        {
            issuerInfo["codigoRetencion"] = "303";
            issuerInfo["porcentajeRetencion"] = "1";
            issuerInfo["valorRetencion"] = "0.20";
        }

        var providerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacionProveedor"] = "05",
            ["razonSocialProveedor"] = "PROVEEDOR PERSONA NATURAL",
            ["identificacionProveedor"] = "1712345678",
            ["direccionProveedor"] = "Quito, Calle Ejemplo 123"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.LiquidacionCompra, issuerInfo, providerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "SERV001",
            description: "Servicio prestado",
            quantity: 1m,
            unitPrice: 20.00m,
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
        var document = CreateTestLiquidacionCompra();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("liquidacionCompra");
    }

    [Fact]
    public async Task GenerateXmlAsync_RootElementVersion_Is1_1_0()
    {
        var document = CreateTestLiquidacionCompra();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("1.1.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoTributariaCodDoc_Is03()
    {
        var document = CreateTestLiquidacionCompra();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("codDoc")!.Value.Should().Be("03");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoLiquidacionCompra_HasProviderFields()
    {
        var document = CreateTestLiquidacionCompra();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoLiquidacion = xDoc.Root!.Element("infoLiquidacionCompra");
        infoLiquidacion.Should().NotBeNull();
        infoLiquidacion!.Element("tipoIdentificacionProveedor")!.Value.Should().Be("05");
        infoLiquidacion.Element("razonSocialProveedor")!.Value.Should().Be("PROVEEDOR PERSONA NATURAL");
        infoLiquidacion.Element("identificacionProveedor")!.Value.Should().Be("1712345678");
        infoLiquidacion.Element("direccionProveedor")!.Value.Should().Be("Quito, Calle Ejemplo 123");
    }

    [Fact]
    public async Task GenerateXmlAsync_Retencion_PresentWhenCodigoRetencionProvided()
    {
        var document = CreateTestLiquidacionCompra(withRetencion: true);

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoLiquidacion = xDoc.Root!.Element("infoLiquidacionCompra")!;
        var retencion = infoLiquidacion.Element("retencion");
        retencion.Should().NotBeNull();
        retencion!.Element("codigo")!.Value.Should().Be("303");
    }

    [Fact]
    public async Task GenerateXmlAsync_Retencion_AbsentWhenNoCodigoRetencion()
    {
        var document = CreateTestLiquidacionCompra(withRetencion: false);

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoLiquidacion = xDoc.Root!.Element("infoLiquidacionCompra")!;
        infoLiquidacion.Element("retencion").Should().BeNull();
    }

    [Fact]
    public async Task GenerateXmlAsync_Detalles_HasItemElements()
    {
        var document = CreateTestLiquidacionCompra();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var detalles = xDoc.Root!.Element("detalles");
        detalles.Should().NotBeNull();

        var detalle = detalles!.Elements("detalle").ToList();
        detalle.Should().HaveCount(1);
        detalle[0].Element("codigoPrincipal")!.Value.Should().Be("SERV001");
        detalle[0].Element("descripcion")!.Value.Should().Be("Servicio prestado");
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
            .WithMessage("*LiquidacionCompra*");
    }
}
