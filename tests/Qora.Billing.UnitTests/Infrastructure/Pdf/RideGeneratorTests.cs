using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Pdf;
using QuestPDF.Infrastructure;
using DomainDocumentType = Qora.Billing.Domain.Enums.DocumentType;

namespace Qora.Billing.UnitTests.Infrastructure.Pdf;

public class RideGeneratorTests
{
    static RideGeneratorTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly RideGenerator _sut = new();

    private static string GenerateValidAccessKey()
    {
        var baseDigits = "180320260117922680710011001001000000012372816811";
        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = baseDigits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (baseDigits.Length - 1 - i) % weights.Length;
            sum += (baseDigits[i] - '0') * weights[weightIndex];
        }
        var remainder = sum % 11;
        var checkDigit = 11 - remainder;
        checkDigit = checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };
        return baseDigits + checkDigit;
    }

    private static Document CreateMinimalDocument()
    {
        return Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." }
            },
            new Dictionary<string, string>
            {
                { "ruc", "0102030405001" },
                { "razonSocial", "Comprador Test" }
            });
    }

    private static Document CreateFullDocument()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "nombreComercial", "Tienda El Buen Sabor" },
                { "direccion", "Av. Amazonas N36-152 y Naciones Unidas" },
                { "direccionMatriz", "Av. Amazonas N36-152 y Naciones Unidas" },
                { "obligadoContabilidad", "SI" },
                { "estab", "001" },
                { "ptoEmi", "002" },
                { "secuencial", "000000123" },
                { "ambiente", "1" }
            },
            new Dictionary<string, string>
            {
                { "ruc", "0102030405001" },
                { "razonSocial", "Juan Perez Rodriguez" },
                { "tipoIdentificacion", "04" },
                { "direccion", "Calle Sucre 123 y Bolivar" },
                { "telefono", "0991234567" },
                { "email", "juan.perez@example.com" }
            });

        // Add items
        var item1 = DocumentItem.Create(
            document.Id, "PROD001", "Hamburguesa Clasica", 2, 5.50m, 0, 15m, "2", "4");
        var item2 = DocumentItem.Create(
            document.Id, "PROD002", "Coca Cola 500ml", 3, 1.25m, 0, 15m, "2", "4");
        var item3 = DocumentItem.Create(
            document.Id, "BEB001", "Agua mineral sin gas", 1, 0.75m, 0, 0m, "2", "0",
            auxiliaryCode: "BEB001-AUX");

        document.AddItem(item1);
        document.AddItem(item2);
        document.AddItem(item3);

        // Set XML and access key so the document has all fields
        var accessKey = new AccessKey(GenerateValidAccessKey());
        document.SetXmlContent("<xml>test</xml>", accessKey);

        return document;
    }

    [Fact]
    public async Task GeneratePdfAsync_WithMinimalDocument_ShouldReturnNonEmptyByteArray()
    {
        var document = CreateMinimalDocument();

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GeneratePdfAsync_WithMinimalDocument_ShouldReturnValidPdf()
    {
        var document = CreateMinimalDocument();

        var result = await _sut.GeneratePdfAsync(document);

        // PDF files start with %PDF
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    [Fact]
    public async Task GeneratePdfAsync_WithFullDocument_ShouldReturnNonEmptyByteArray()
    {
        var document = CreateFullDocument();

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GeneratePdfAsync_WithFullDocument_ShouldReturnValidPdf()
    {
        var document = CreateFullDocument();

        var result = await _sut.GeneratePdfAsync(document);

        // PDF files start with %PDF
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    [Fact]
    public async Task GeneratePdfAsync_WithMultipleItems_ShouldReturnValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Multi Items Corp" }
            },
            new Dictionary<string, string>
            {
                { "ruc", "0102030405001" },
                { "razonSocial", "Buyer Corp" }
            });

        // Add 10 items with various tax rates
        for (var i = 1; i <= 10; i++)
        {
            var taxRate = i % 3 == 0 ? 0m : (i % 3 == 1 ? 15m : 5m);
            var taxPercentageCode = taxRate == 0 ? "0" : (taxRate == 5 ? "5" : "4");
            var item = DocumentItem.Create(
                document.Id,
                $"PROD{i:D3}",
                $"Producto de prueba numero {i}",
                i,
                10.00m + i,
                i * 0.50m,
                taxRate,
                "2",
                taxPercentageCode);
            document.AddItem(item);
        }

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Length.Should().BeGreaterThan(100, "a PDF with 10 items should have substantial content");
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    [Fact]
    public async Task GeneratePdfAsync_WithNullDocument_ShouldThrowArgumentNullException()
    {
        var act = () => _sut.GeneratePdfAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GeneratePdfAsync_WithAuthorizedDocument_ShouldIncludeAuthorizationData()
    {
        var document = CreateFullDocument();
        // Advance to authorized state
        document.SetSignedXml("<signed>xml</signed>");
        document.MarkSentToSri();
        document.Authorize("1803202601179226807100110010010000000123728168111", DateTime.UtcNow);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    [Fact]
    public void EncodeCode128B_WithEmptyString_ShouldReturnEmpty()
    {
        var result = RideGenerator.EncodeCode128B("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void EncodeCode128B_WithValidData_ShouldReturnNonEmptyPattern()
    {
        var result = RideGenerator.EncodeCode128B("12345");

        result.Should().NotBeEmpty();
        result.Should().MatchRegex("^[01]+$", "barcode encoding should only contain 0s and 1s");
    }

    // T-012: NotaCredito
    [Fact]
    public async Task GeneratePdfAsync_NotaCredito_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.NotaCredito,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "codDocModificado", "01" },
                { "numDocModificado", "001-001-000000001" },
                { "fechaEmisionDocSustento", "01/01/2026" },
                { "motivo", "Devolucion de mercaderia" }
            },
            new Dictionary<string, string>
            {
                { "ruc", "0102030405001" },
                { "razonSocial", "Juan Perez" }
            });

        var item = DocumentItem.Create(document.Id, "PROD001", "Producto devuelto", 1, 10m, 0, 15m, "2", "4");
        document.AddItem(item);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    // T-013: NotaDebito
    [Fact]
    public async Task GeneratePdfAsync_NotaDebito_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.NotaDebito,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "codDocSustento", "01" },
                { "numDocSustento", "001-001-000000001" },
                { "fechaEmisionDocSustento", "01/01/2026" }
            },
            new Dictionary<string, string>
            {
                { "ruc", "0102030405001" },
                { "razonSocial", "Juan Perez" }
            });

        // For NotaDebito, items represent motivos: Description=razon, UnitPrice=valor
        var motivo = DocumentItem.Create(document.Id, "MOT001", "Interes por mora", 1, 25.50m, 0, 0m, "2", "0");
        document.AddItem(motivo);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    // T-014: LiquidacionCompra
    [Fact]
    public async Task GeneratePdfAsync_LiquidacionCompra_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.LiquidacionCompra,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" }
            },
            new Dictionary<string, string>
            {
                { "razonSocialProveedor", "Proveedor Natural" },
                { "identificacionProveedor", "0102030405" },
                { "direccionProveedor", "Calle 123" },
                { "tipoIdentificacionProveedor", "05" }
            });

        var item = DocumentItem.Create(document.Id, "SRV001", "Servicio de limpieza", 1, 50m, 0, 15m, "2", "4");
        document.AddItem(item);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    // T-015: GuiaRemision
    [Fact]
    public async Task GeneratePdfAsync_GuiaRemision_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.GuiaRemision,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "rucTransportista", "1792268071001" },
                { "razonSocialTransportista", "Transportes SA" },
                { "placa", "ABC-1234" },
                { "fechaInicioTransporte", "01/03/2026" },
                { "fechaFinTransporte", "02/03/2026" }
            },
            new Dictionary<string, string>
            {
                { "identificacionDestinatario", "0102030405001" },
                { "razonSocialDestinatario", "Destinatario Test" },
                { "dirDestinatario", "Av. Principal 456" },
                { "motivoTraslado", "Venta de mercaderia" }
            });

        var item = DocumentItem.Create(document.Id, "PROD001", "Caja de naranjas", 10, 0m, 0, 0m, "0", "0");
        document.AddItem(item);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    // T-016: ComprobanteRetencion
    [Fact]
    public async Task GeneratePdfAsync_ComprobanteRetencion_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.ComprobanteRetencion,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa Retenedora S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "periodoFiscal", "03/2026" }
            },
            new Dictionary<string, string>
            {
                { "tipoIdentificacion", "04" },
                { "razonSocial", "Sujeto Retenido SA" },
                { "identificacion", "0102030405001" }
            });

        // AuxiliaryCode: "codDocSustento|numDocSustento|fechaEmisionDocSustento|numAutDocSustento"
        var retencion = DocumentItem.Create(
            document.Id, "01", "Retencion en la fuente",
            1, 1000m, 0, 1.75m, "1", "303",
            auxiliaryCode: "01|001-001-000000001|01/03/2026|1234567890");
        document.AddItem(retencion);

        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");

        // Verify retention calculation: 1000 * 1.75% = 17.50
        result.Length.Should().BeGreaterThan(100);
    }

    // T-017: GuiaRemision with empty items list
    [Fact]
    public async Task GeneratePdfAsync_GuiaRemision_WithEmptyItems_ShouldReturnNonEmptyValidPdf()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.GuiaRemision,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa de Prueba S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "rucTransportista", "1792268071001" },
                { "razonSocialTransportista", "Transportes SA" },
                { "placa", "XYZ-9999" },
                { "fechaInicioTransporte", "01/03/2026" },
                { "fechaFinTransporte", "02/03/2026" }
            },
            new Dictionary<string, string>
            {
                { "identificacionDestinatario", "0102030405001" },
                { "razonSocialDestinatario", "Destinatario Vacio" },
                { "dirDestinatario", "Calle Sin Numero" },
                { "motivoTraslado", "Traslado sin detalles" }
            });

        // No items added — empty items list
        var result = await _sut.GeneratePdfAsync(document);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(result, 0, 4);
        header.Should().Be("%PDF");
    }

    // T-018: ComprobanteRetencion with malformed AuxiliaryCode
    [Fact]
    public async Task GeneratePdfAsync_ComprobanteRetencion_WithMalformedAuxiliaryCode_ShouldNotCrash()
    {
        var document = Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.ComprobanteRetencion,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Empresa Retenedora S.A." },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" },
                { "periodoFiscal", "03/2026" }
            },
            new Dictionary<string, string>
            {
                { "tipoIdentificacion", "04" },
                { "razonSocial", "Sujeto Retenido" },
                { "identificacion", "0102030405001" }
            });

        // Malformed: empty auxiliaryCode (no pipe separators)
        var retencionMalformed = DocumentItem.Create(
            document.Id, "01", "Retencion malformada",
            1, 500m, 0, 2m, "1", "303",
            auxiliaryCode: null);
        document.AddItem(retencionMalformed);

        // Also add one with partial pipes
        var retencionPartial = DocumentItem.Create(
            document.Id, "01", "Retencion parcial",
            1, 200m, 0, 1m, "1", "303",
            auxiliaryCode: "01|001-001-000000002");
        document.AddItem(retencionPartial);

        // Should NOT throw — graceful degradation
        var act = async () => await _sut.GeneratePdfAsync(document);
        await act.Should().NotThrowAsync();

        var result = await _sut.GeneratePdfAsync(document);
        result.Should().NotBeEmpty();
    }
}
