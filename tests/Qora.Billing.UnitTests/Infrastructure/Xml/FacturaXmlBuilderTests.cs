using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class FacturaXmlBuilderTests
{
    private readonly FacturaXmlBuilder _builder = new();

    private static Document CreateTestFactura()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ambiente"] = "1",
            ["tipoEmision"] = "1",
            ["razonSocial"] = "EMPRESA TEST S.A.",
            ["nombreComercial"] = "Test Shop",
            ["ruc"] = "1792268071001",
            ["claveAcceso"] = "1803202601179226807100110010010000000123728168114",
            ["codDoc"] = "01",
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000012",
            ["dirMatriz"] = "Quito, Av. Amazonas",
            ["fechaEmision"] = "18/03/2026",
            ["dirEstablecimiento"] = "Quito, Local 1",
            ["obligadoContabilidad"] = "SI"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CONSUMIDOR FINAL",
            ["identificacion"] = "9999999999999",
            ["direccion"] = "Quito",
            ["email"] = "test@example.com",
            ["formaPago"] = "01"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.Factura, issuerInfo, buyerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "PROD001",
            description: "Producto de prueba",
            quantity: 2m,
            unitPrice: 10.00m,
            discount: 0m,
            taxRate: 15m,
            taxCode: "2",
            taxPercentageCode: "4",
            auxiliaryCode: "AUX001");

        document.AddItem(item);
        return document;
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldProduceValidXml()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("factura");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainInfoTributaria()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("ruc")!.Value.Should().Be("1792268071001");
        infoTributaria.Element("razonSocial")!.Value.Should().Be("EMPRESA TEST S.A.");
        infoTributaria.Element("codDoc")!.Value.Should().Be("01");
        infoTributaria.Element("estab")!.Value.Should().Be("001");
        infoTributaria.Element("ptoEmi")!.Value.Should().Be("001");
        infoTributaria.Element("secuencial")!.Value.Should().Be("000000012");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainInfoFactura()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoFactura = xDoc.Root!.Element("infoFactura");
        infoFactura.Should().NotBeNull();
        infoFactura!.Element("fechaEmision")!.Value.Should().Be("18/03/2026");
        infoFactura.Element("tipoIdentificacionComprador")!.Value.Should().Be("04");
        infoFactura.Element("razonSocialComprador")!.Value.Should().Be("CONSUMIDOR FINAL");
        infoFactura.Element("identificacionComprador")!.Value.Should().Be("9999999999999");
        infoFactura.Element("moneda")!.Value.Should().Be("DOLAR");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldCalculateTotalsCorrectly()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoFactura = xDoc.Root!.Element("infoFactura")!;

        // 2 * 10.00 - 0 = 20.00 subtotal
        infoFactura.Element("totalSinImpuestos")!.Value.Should().Be("20.00");
        infoFactura.Element("totalDescuento")!.Value.Should().Be("0.00");

        // 20.00 + (20.00 * 15%) = 20.00 + 3.00 = 23.00
        infoFactura.Element("importeTotal")!.Value.Should().Be("23.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainDetalles()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var detalles = xDoc.Root!.Element("detalles");
        detalles.Should().NotBeNull();

        var detalle = detalles!.Elements("detalle").ToList();
        detalle.Should().HaveCount(1);

        detalle[0].Element("codigoPrincipal")!.Value.Should().Be("PROD001");
        detalle[0].Element("descripcion")!.Value.Should().Be("Producto de prueba");
        detalle[0].Element("cantidad")!.Value.Should().Be("2.000000");
        detalle[0].Element("precioUnitario")!.Value.Should().Be("10.000000");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainTaxInformation()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var totalImpuesto = xDoc.Root!.Element("infoFactura")!
            .Element("totalConImpuestos")!
            .Elements("totalImpuesto").ToList();

        totalImpuesto.Should().HaveCount(1);
        totalImpuesto[0].Element("codigo")!.Value.Should().Be("2");
        totalImpuesto[0].Element("codigoPorcentaje")!.Value.Should().Be("4");
        totalImpuesto[0].Element("baseImponible")!.Value.Should().Be("20.00");
        totalImpuesto[0].Element("valor")!.Value.Should().Be("3.00"); // 20 * 15%
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainInfoAdicional_WhenBuyerHasEmail()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoAdicional = xDoc.Root!.Element("infoAdicional");
        infoAdicional.Should().NotBeNull();

        var campos = infoAdicional!.Elements("campoAdicional").ToList();
        campos.Should().Contain(c => c.Attribute("nombre")!.Value == "email");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldHaveUtf8Encoding()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().Contain("encoding=\"utf-8\"");
    }

    [Fact]
    public async Task GenerateXmlAsync_WithNonFacturaType_ShouldThrow()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DocumentType.NotaCredito,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var act = () => _builder.GenerateXmlAsync(document);

        await act.Should().ThrowAsync<Qora.Billing.Domain.Exceptions.DocumentValidationException>()
            .WithMessage("*Factura*");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldHaveVersionAttribute()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("2.1.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldContainPagos()
    {
        var document = CreateTestFactura();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var pagos = xDoc.Root!.Element("infoFactura")!.Element("pagos");
        pagos.Should().NotBeNull();

        var pago = pagos!.Element("pago")!;
        pago.Element("formaPago")!.Value.Should().Be("01");
        pago.Element("total")!.Value.Should().Be("23.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_WithMultipleItems_ShouldGroupTaxes()
    {
        var issuerInfo = new Dictionary<string, string>
        {
            ["ambiente"] = "1", ["tipoEmision"] = "1", ["razonSocial"] = "TEST",
            ["ruc"] = "1792268071001", ["claveAcceso"] = "1803202601179226807100110010010000000123728168114",
            ["estab"] = "001", ["ptoEmi"] = "001", ["secuencial"] = "000000001",
            ["dirMatriz"] = "Quito", ["fechaEmision"] = "18/03/2026"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04", ["razonSocial"] = "BUYER",
            ["identificacion"] = "9999999999999"
        };

        var doc = Document.Create(Guid.NewGuid(), DocumentType.Factura, issuerInfo, buyerInfo);

        // Item with 15% IVA
        doc.AddItem(DocumentItem.Create(doc.Id, "A1", "Item 15%", 1, 100, 0, 15, "2", "4"));
        // Item with 0% IVA
        doc.AddItem(DocumentItem.Create(doc.Id, "A2", "Item 0%", 1, 50, 0, 0, "2", "0"));

        var xml = await _builder.GenerateXmlAsync(doc);
        var xDoc = XDocument.Parse(xml);

        var taxGroups = xDoc.Root!.Element("infoFactura")!
            .Element("totalConImpuestos")!
            .Elements("totalImpuesto").ToList();

        taxGroups.Should().HaveCount(2);
    }
}
