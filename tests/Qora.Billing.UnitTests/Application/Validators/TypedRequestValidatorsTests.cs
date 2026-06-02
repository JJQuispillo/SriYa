using FluentValidation.TestHelper;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.DTOs.Requests;
using Qora.Billing.Application.Validators.Requests;

namespace Qora.Billing.UnitTests.Application.Validators;

/// <summary>
/// Tests de los 6 validadores tipados por tipo de documento: caso válido + casos inválidos clave.
/// </summary>
public class TypedRequestValidatorsTests
{
    private static EmisorBaseDto EmisorBase(string ruc = "1792268071001", string razon = "Test Corp") =>
        new(ruc, razon, "Av. Quito 123", "001", "001", "000000001");

    private static CompradorDto Comprador(string id = "0102030405001") =>
        new("04", "Buyer Corp", id);

    private static ItemDto Item(decimal precio = 50m, string imp = "2", string pct = "4") =>
        new("PROD001", "Producto", 1, precio, 0m, imp, pct);

    // ---------- Factura ----------

    [Fact]
    public void Factura_Valid_Passes()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Factura_RucNot13Digits_Fails()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(ruc: "123"), Comprador(), [Item()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Ruc")
            .WithErrorMessage("El RUC del emisor debe tener exactamente 13 dígitos.");
    }

    [Fact]
    public void Factura_MissingRazonSocial_Fails()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(razon: ""), Comprador(), [Item()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("RazonSocial")
            .WithErrorMessage("La información del emisor debe contener la razón social.");
    }

    [Fact]
    public void Factura_Over200WithoutIdentificacion_Fails()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(id: ""), [Item(precio: 250m)]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("comprador.identificacion")
            .WithErrorMessage("La identificación del comprador es requerida para comprobantes que superen los $200.");
    }

    [Fact]
    public void Factura_Under200WithoutIdentificacion_Passes()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(id: ""), [Item(precio: 100m)]);

        var result = validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("comprador.identificacion");
    }

    [Fact]
    public void Factura_InvalidTaxCodePair_Fails()
    {
        var validator = new CreateFacturaRequestValidator();
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item(imp: "2", pct: "99")]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("detalles[0].codigoPorcentaje")
            .WithErrorMessage("El código de impuesto '2/99' no es válido según la tabla de códigos SRI.");
    }

    // ---------- LiquidacionCompra ----------

    [Fact]
    public void LiquidacionCompra_Valid_Passes()
    {
        var validator = new CreateLiquidacionCompraRequestValidator();
        var proveedor = new ProveedorDto("04", "Proveedor SA", "1790012345001", "Av. Proveedor 1");
        var request = new CreateLiquidacionCompraRequest(EmisorBase(), proveedor, [Item()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void LiquidacionCompra_InvalidProveedorIdType_Fails()
    {
        var validator = new CreateLiquidacionCompraRequestValidator();
        var proveedor = new ProveedorDto("99", "Proveedor SA", "1790012345001", "Av. Proveedor 1");
        var request = new CreateLiquidacionCompraRequest(EmisorBase(), proveedor, [Item()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Proveedor.TipoIdentificacionProveedor");
    }

    [Fact]
    public void LiquidacionCompra_PartialRetencion_Fails()
    {
        var validator = new CreateLiquidacionCompraRequestValidator();
        var proveedor = new ProveedorDto("04", "Proveedor SA", "1790012345001", "Av. Proveedor 1");
        var retencion = new RetencionEmisorDto("303", "", "5.00"); // porcentaje vacío
        var request = new CreateLiquidacionCompraRequest(EmisorBase(), proveedor, [Item()], retencion);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Retencion.PorcentajeRetencion");
    }

    // ---------- NotaCredito ----------

    [Fact]
    public void NotaCredito_Valid_Passes()
    {
        var validator = new CreateNotaCreditoRequestValidator();
        var sustento = new DocSustentoModificacionDto("01", "001-001-000000123", new DateTime(2026, 3, 9), "Devolución");
        var request = new CreateNotaCreditoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void NotaCredito_InvalidCodDocModificado_Fails()
    {
        var validator = new CreateNotaCreditoRequestValidator();
        var sustento = new DocSustentoModificacionDto("99", "001-001-000000123", new DateTime(2026, 3, 9), "Devolución");
        var request = new CreateNotaCreditoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Sustento.CodDocModificado");
    }

    // ---------- NotaDebito ----------

    [Fact]
    public void NotaDebito_Valid_Passes()
    {
        var validator = new CreateNotaDebitoRequestValidator();
        var sustento = new DocSustentoDto("01", "001-001-000000999", new DateTime(2026, 12, 1));
        var request = new CreateNotaDebitoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void NotaDebito_MissingNumDocSustento_Fails()
    {
        var validator = new CreateNotaDebitoRequestValidator();
        var sustento = new DocSustentoDto("01", "", new DateTime(2026, 12, 1));
        var request = new CreateNotaDebitoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Sustento.NumDocSustento");
    }

    // ---------- GuiaRemision ----------

    private static EmisorGuiaDto EmisorGuia() =>
        new("1792268071001", "Test Corp", "Av. Quito 123", "001", "001", "000000001",
            "Transportes SA", "1790012345001", "SI", "09/03/2026", "10/03/2026", "PBA1234");

    private static DestinatarioDto Destinatario() =>
        new("0102030405001", "Destino SA", "Av. Amazonas", "Venta", "1790012345001",
            [new DestinatarioItemDto("PROD001", "Producto", 1)]);

    [Fact]
    public void GuiaRemision_Valid_Passes()
    {
        var validator = new CreateGuiaRemisionRequestValidator();
        var request = new CreateGuiaRemisionRequest(EmisorGuia(), new GuiaSustentoDto("01", "001-001-000000123"), [Destinatario()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GuiaRemision_MissingDestinatarios_Fails()
    {
        var validator = new CreateGuiaRemisionRequestValidator();
        var request = new CreateGuiaRemisionRequest(EmisorGuia(), new GuiaSustentoDto("01", "001-001-000000123"), []);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Destinatarios")
            .WithErrorMessage("Se requiere al menos un destinatario.");
    }

    // ---------- ComprobanteRetencion ----------

    private static EmisorRetencionDto EmisorRetencion(string periodo = "03/2026") =>
        new("1792268071001", "Test Corp", "Av. Quito 123", "001", "001", "000000001", periodo);

    private static RetencionItemDto RetencionItem(
        string tipoSustento = "01", string numSustento = "001-001-000000123",
        string numAut = "1234567890") =>
        new("RET001", "Retención Renta", 1, 100m, 0m, "1", "303",
            tipoSustento, numSustento, new DateTime(2026, 2, 28), numAut);

    [Fact]
    public void Retencion_Valid_Passes()
    {
        var validator = new CreateComprobanteRetencionRequestValidator();
        var request = new CreateComprobanteRetencionRequest(EmisorRetencion(), Comprador(), [RetencionItem()]);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Retencion_MissingSustentoFields_Fails()
    {
        var validator = new CreateComprobanteRetencionRequestValidator();
        var request = new CreateComprobanteRetencionRequest(
            EmisorRetencion(), Comprador(), [RetencionItem(numSustento: "", numAut: "")]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("detalles[0].numDocSustento")
            .WithErrorMessage("El número del documento de sustento es requerido para ítems de ComprobanteRetencion.");
        result.ShouldHaveValidationErrorFor("detalles[0].numAutDocSustento");
    }

    [Fact]
    public void Retencion_InvalidPeriodoFiscal_Fails()
    {
        var validator = new CreateComprobanteRetencionRequestValidator();
        var request = new CreateComprobanteRetencionRequest(EmisorRetencion(periodo: "2026-03"), Comprador(), [RetencionItem()]);

        var result = validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Emisor.PeriodoFiscal")
            .WithErrorMessage("El periodo fiscal debe tener el formato MM/YYYY.");
    }
}
