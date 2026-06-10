using FluentAssertions;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.DTOs.Requests;
using Qora.Billing.Application.Mapping;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.UnitTests.Application.Mapping;

/// <summary>
/// Verifica que el mapeo typed → CreateDocumentRequest produzca exactamente las claves de
/// IssuerInfo/BuyerInfo que leen los XML builders (Xml/{Type}Constants.cs), preservando el
/// contrato de diccionarios previo. CRÍTICO: las fechas de sustento deben formatearse como
/// "dd/MM/yyyy" (InvariantCulture), idéntico al string que el contrato anterior esperaba.
/// </summary>
public class DocumentRequestMappersTests
{
    private static EmisorBaseDto EmisorBase() =>
        new("1792268071001", "Test Corp", "Av. Quito 123", "001", "001", "000000001");

    private static CompradorDto Comprador() =>
        new("04", "Buyer Corp", "0102030405001", Email: "buyer@example.com");

    private static ItemDto Item() =>
        new("PROD001", "Producto", 1, 50m, 0m, "2", "4");

    [Fact]
    public void Factura_MapsEmisorAndCompradorToDicts()
    {
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.Factura);
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("ruc", "1792268071001"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("razonSocial", "Test Corp"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("dirMatriz", "Av. Quito 123"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("estab", "001"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("ptoEmi", "001"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("secuencial", "000000001"));

        result.Comprador.Should().Contain(new KeyValuePair<string, string>("tipoIdentificacion", "04"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("razonSocial", "Buyer Corp"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("identificacion", "0102030405001"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("email", "buyer@example.com"));

        result.Detalles.Should().HaveCount(1);
        result.Detalles[0].CodigoPrincipal.Should().Be("PROD001");
        result.Detalles[0].CodigoImpuesto.Should().Be("2");
        result.Detalles[0].CodigoPorcentaje.Should().Be("4");
    }

    [Fact]
    public void Factura_DoesNotSetSystemGeneratedKeys()
    {
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.Emisor.Keys.Should().NotContain(["ambiente", "tipoEmision", "claveAcceso", "fechaEmision"]);
    }

    [Fact]
    public void Factura_SecuencialNull_OmitsKeyFromIssuerInfo()
    {
        // T-SEC-025 (Change #3 R6/D9): en modo AUTO el emisor no envía secuencial (null). El mapper debe
        // OMITIR la clave 'secuencial' del diccionario (AddOptional), nunca escribir null (el valor del
        // diccionario IssuerInfo es string no-nullable y el servidor lo asigna después en el handler).
        var emisor = new EmisorBaseDto("1792268071001", "Test Corp", "Av. Quito 123", "001", "001", Secuencial: null);
        var request = new CreateFacturaRequest(emisor, Comprador(), [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.Emisor.Keys.Should().NotContain("secuencial", "AUTO mode: la clave se omite, no se escribe null");
        // Las otras claves de identidad de negocio SÍ se escriben.
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("estab", "001"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("ptoEmi", "001"));
    }

    [Fact]
    public void Factura_SecuencialPresent_WritesKeyIntoIssuerInfo()
    {
        // T-SEC-025 (complemento): cuando el secuencial está presente (modo CLIENT) la clave SÍ se escribe.
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.Emisor.Should().Contain(new KeyValuePair<string, string>("secuencial", "000000001"));
    }

    [Fact]
    public void NotaCredito_MapsSustentoIntoIssuerInfoWithSriDateFormat()
    {
        var fecha = new DateTime(2026, 3, 9);
        var sustento = new DocSustentoModificacionDto("01", "001-001-000000123", fecha, "Devolución parcial");
        var request = new CreateNotaCreditoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.NotaCredito);
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("codDocModificado", "01"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("numDocModificado", "001-001-000000123"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("razonModificacion", "Devolución parcial"));
        // Contrato de fecha: dd/MM/yyyy InvariantCulture, idéntico al string previo.
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("fechaEmisionDocSustento", "09/03/2026"));
    }

    [Fact]
    public void NotaDebito_MapsSustentoIntoIssuerInfoWithSriDateFormat()
    {
        var fecha = new DateTime(2026, 12, 1);
        var sustento = new DocSustentoDto("01", "001-001-000000999", fecha);
        var request = new CreateNotaDebitoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.NotaDebito);
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("codDocSustento", "01"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("numDocSustento", "001-001-000000999"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("fechaEmisionDocSustento", "01/12/2026"));
    }

    [Fact]
    public void GuiaRemision_MapsSustentoIntoBuyerInfoAndTransporterIntoIssuerInfo()
    {
        var emisor = new EmisorGuiaDto(
            "1792268071001", "Test Corp", "Av. Quito 123", "001", "001", "000000001",
            RazonSocialTransportista: "Transportes SA",
            RucTransportista: "1790012345001",
            ObligadoContabilidad: "SI",
            FechaInicioTransporte: "09/03/2026",
            FechaFinTransporte: "10/03/2026",
            Placa: "PBA1234");
        var sustento = new GuiaSustentoDto("01", "001-001-000000123");
        var destinatario = new DestinatarioDto(
            "0102030405001", "Destino SA", "Av. Amazonas", "Venta", "1790012345001",
            [new DestinatarioItemDto("PROD001", "Producto", 1)]);
        var request = new CreateGuiaRemisionRequest(emisor, sustento, [destinatario]);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.GuiaRemision);
        // Sustento a nivel documento → BuyerInfo (GuiaRemisionXmlBuilder lee desde buyer).
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("codDocSustento", "01"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("numDocSustento", "001-001-000000123"));
        // Transportista → IssuerInfo.
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("razonSocialTransportista", "Transportes SA"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("rucTransportista", "1790012345001"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("obligadoContabilidad", "SI"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("placa", "PBA1234"));
        // Destinatarios se pasan tal cual; sin ítems a nivel documento.
        result.Detalles.Should().BeEmpty();
        result.Destinatarios.Should().ContainSingle();
        result.Destinatarios![0].IdentificacionDestinatario.Should().Be("0102030405001");
    }

    [Fact]
    public void LiquidacionCompra_MapsProveedorToBuyerInfoAndRetencionToIssuerInfo()
    {
        var proveedor = new ProveedorDto("04", "Proveedor SA", "1790012345001", "Av. Proveedor 1");
        var retencion = new RetencionEmisorDto("303", "10", "5.00");
        var request = new CreateLiquidacionCompraRequest(EmisorBase(), proveedor, [Item()], retencion);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.LiquidacionCompra);
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("tipoIdentificacionProveedor", "04"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("razonSocialProveedor", "Proveedor SA"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("identificacionProveedor", "1790012345001"));
        result.Comprador.Should().Contain(new KeyValuePair<string, string>("direccionProveedor", "Av. Proveedor 1"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("codigoRetencion", "303"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("porcentajeRetencion", "10"));
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("valorRetencion", "5.00"));
    }

    [Fact]
    public void Retencion_MapsPeriodoFiscalAndPerItemSustentoWithSriDateFormat()
    {
        var emisor = new EmisorRetencionDto(
            "1792268071001", "Test Corp", "Av. Quito 123", "001", "001", "000000001", "03/2026");
        var fecha = new DateTime(2026, 2, 28);
        var item = new RetencionItemDto(
            "RET001", "Retención Renta", 1, 100m, 0m, "1", "303",
            TipoDocSustento: "01",
            NumDocSustento: "001-001-000000123",
            FechaEmisionDocSustento: fecha,
            NumAutDocSustento: "1234567890");
        var request = new CreateComprobanteRetencionRequest(emisor, Comprador(), [item]);

        var result = request.ToCreateDocumentRequest();

        result.TipoDocumento.Should().Be(DocumentType.ComprobanteRetencion);
        result.Emisor.Should().Contain(new KeyValuePair<string, string>("periodoFiscal", "03/2026"));

        result.Detalles.Should().ContainSingle();
        var mapped = result.Detalles[0];
        mapped.TipoDocSustento.Should().Be("01");
        mapped.NumDocSustento.Should().Be("001-001-000000123");
        mapped.NumAutDocSustento.Should().Be("1234567890");
        // El sustento por ítem conserva la fecha como DateTime (la formatea el XML builder).
        mapped.FechaEmisionDocSustento.Should().Be(fecha);
    }
}
