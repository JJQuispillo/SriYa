using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure.Xml;

public class NotaDebitoXmlBuilderTests
{
    private readonly NotaDebitoXmlBuilder _builder = new();

    private static Document CreateTestNotaDebito()
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
            ["codDocSustento"] = "01",
            ["numDocSustento"] = "001-001-000000005",
            ["fechaEmisionDocSustento"] = "10/01/2024"
        };

        var buyerInfo = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = "04",
            ["razonSocial"] = "CLIENTE FINAL S.A.",
            ["identificacion"] = "1790012345001",
            ["email"] = "cliente@example.com"
        };

        var document = Document.Create(Guid.NewGuid(), DocumentType.NotaDebito, issuerInfo, buyerInfo);

        var item = DocumentItem.Create(
            document.Id,
            mainCode: "INT001",
            description: "Cobro por intereses",
            quantity: 1m,
            unitPrice: 50.00m,
            discount: 5m,   // discount ignored for NdD totals
            taxRate: 15m,
            taxCode: "2",
            taxPercentageCode: "4");

        document.AddItem(item);
        return document;
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldProduceValidXml()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().NotBeNullOrWhiteSpace();
        var xDoc = XDocument.Parse(xml);
        xDoc.Root!.Name.LocalName.Should().Be("notaDebito");
    }

    [Fact]
    public async Task GenerateXmlAsync_RootElementVersion_Is1_0_0()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        xDoc.Root!.Attribute("version")!.Value.Should().Be("1.0.0");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoTributariaCodDoc_Is05()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoTributaria = xDoc.Root!.Element("infoTributaria");
        infoTributaria.Should().NotBeNull();
        infoTributaria!.Element("codDoc")!.Value.Should().Be("05");
    }

    [Fact]
    public async Task GenerateXmlAsync_Motivos_ContainItems_NotDetalles()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        // Must have <motivos>, NOT <detalles>
        xDoc.Root!.Element("motivos").Should().NotBeNull();
        xDoc.Root.Element("detalles").Should().BeNull();

        var motivos = xDoc.Root.Element("motivos")!.Elements("motivo").ToList();
        motivos.Should().HaveCount(1);
    }

    [Fact]
    public async Task GenerateXmlAsync_Motivo_HasRazonAndValor()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var motivo = xDoc.Root!.Element("motivos")!.Element("motivo")!;
        motivo.Element("razon")!.Value.Should().Be("Cobro por intereses");
        // valor = UnitPrice * Quantity = 50.00 * 1 = 50.00 (no discount)
        motivo.Element("valor")!.Value.Should().Be("50.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_TotalSinImpuestos_ExcludesDiscount()
    {
        // quantity=1, unitPrice=50.00, discount=5 → totalSinImpuestos = UnitPrice*Quantity = 50.00 (NdD ignores discount)
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoNotaDebito = xDoc.Root!.Element("infoNotaDebito")!;
        infoNotaDebito.Element("totalSinImpuestos")!.Value.Should().Be("50.00");
    }

    [Fact]
    public async Task GenerateXmlAsync_infoNotaDebito_HasDocSustentoFields()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);
        var xDoc = XDocument.Parse(xml);

        var infoNotaDebito = xDoc.Root!.Element("infoNotaDebito")!;
        infoNotaDebito.Element("codDocSustento")!.Value.Should().Be("01");
        infoNotaDebito.Element("numDocSustento")!.Value.Should().Be("001-001-000000005");
        infoNotaDebito.Element("fechaEmisionDocSustento")!.Value.Should().Be("10/01/2024");
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
            .WithMessage("*NotaDebito*");
    }

    [Fact]
    public async Task GenerateXmlAsync_ShouldHaveUtf8Encoding()
    {
        var document = CreateTestNotaDebito();

        var xml = await _builder.GenerateXmlAsync(document);

        xml.Should().Contain("encoding=\"utf-8\"");
    }
}
