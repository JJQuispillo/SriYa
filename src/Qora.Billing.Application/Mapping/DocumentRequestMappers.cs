using System.Globalization;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.DTOs.Requests;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.Mapping;

/// <summary>
/// Mapea los requests tipados por tipo de documento al contrato interno
/// <see cref="CreateDocumentRequest"/> (basado en diccionarios) que consume el handler.
///
/// CRÍTICO: las claves de IssuerInfo/BuyerInfo deben coincidir exactamente con las que
/// leen los XML builders (ver Xml/{Type}Constants.cs):
///  - NotaCredito sustento → IssuerInfo (codDocModificado, numDocModificado,
///    fechaEmisionDocSustento, razonModificacion)
///  - NotaDebito sustento → IssuerInfo (codDocSustento, numDocSustento, fechaEmisionDocSustento)
///  - GuiaRemision sustento → BuyerInfo (codDocSustento, numDocSustento)
/// El XML/RIDE/persistencia quedan intactos.
/// </summary>
public static class DocumentRequestMappers
{
    // Formato de fecha SRI (dd/MM/yyyy) usado por los XML builders.
    private const string SriDateFormat = "dd/MM/yyyy";

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateFacturaRequest request)
    {
        var emisor = BuildEmisorBase(request.Emisor);
        var comprador = BuildComprador(request.Comprador);
        return new CreateDocumentRequest(
            DocumentType.Factura,
            emisor,
            comprador,
            MapItems(request.Detalles));
    }

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateLiquidacionCompraRequest request)
    {
        var emisor = BuildEmisorBase(request.Emisor);

        if (request.Retencion is { } ret)
        {
            emisor["codigoRetencion"] = ret.CodigoRetencion;
            emisor["porcentajeRetencion"] = ret.PorcentajeRetencion;
            emisor["valorRetencion"] = ret.ValorRetencion;
        }

        var comprador = new Dictionary<string, string>
        {
            ["tipoIdentificacionProveedor"] = request.Proveedor.TipoIdentificacionProveedor,
            ["razonSocialProveedor"] = request.Proveedor.RazonSocialProveedor,
            ["identificacionProveedor"] = request.Proveedor.IdentificacionProveedor,
            ["direccionProveedor"] = request.Proveedor.DireccionProveedor,
        };

        return new CreateDocumentRequest(
            DocumentType.LiquidacionCompra,
            emisor,
            comprador,
            MapItems(request.Detalles));
    }

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateNotaCreditoRequest request)
    {
        var emisor = BuildEmisorBase(request.Emisor);
        // Sustento → IssuerInfo (NotaCreditoXmlBuilder lee desde issuer).
        emisor["codDocModificado"] = request.Sustento.CodDocModificado;
        emisor["numDocModificado"] = request.Sustento.NumDocModificado;
        emisor["fechaEmisionDocSustento"] = FormatDate(request.Sustento.FechaEmisionDocSustento);
        emisor["razonModificacion"] = request.Sustento.RazonModificacion;

        return new CreateDocumentRequest(
            DocumentType.NotaCredito,
            emisor,
            BuildComprador(request.Comprador),
            MapItems(request.Detalles));
    }

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateNotaDebitoRequest request)
    {
        var emisor = BuildEmisorBase(request.Emisor);
        // Sustento → IssuerInfo (NotaDebitoXmlBuilder lee desde issuer).
        emisor["codDocSustento"] = request.Sustento.CodDocSustento;
        emisor["numDocSustento"] = request.Sustento.NumDocSustento;
        emisor["fechaEmisionDocSustento"] = FormatDate(request.Sustento.FechaEmisionDocSustento);

        return new CreateDocumentRequest(
            DocumentType.NotaDebito,
            emisor,
            BuildComprador(request.Comprador),
            MapItems(request.Detalles));
    }

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateGuiaRemisionRequest request)
    {
        var emisor = new Dictionary<string, string>
        {
            ["ruc"] = request.Emisor.Ruc,
            ["razonSocial"] = request.Emisor.RazonSocial,
            ["dirMatriz"] = request.Emisor.DirMatriz,
            ["estab"] = request.Emisor.Estab,
            ["ptoEmi"] = request.Emisor.PtoEmi,
            ["secuencial"] = request.Emisor.Secuencial,
            // Transportista (obligatorio) → IssuerInfo.
            ["razonSocialTransportista"] = request.Emisor.RazonSocialTransportista,
            ["rucTransportista"] = request.Emisor.RucTransportista,
            ["obligadoContabilidad"] = request.Emisor.ObligadoContabilidad,
            ["fechaInicioTransporte"] = request.Emisor.FechaInicioTransporte,
            ["fechaFinTransporte"] = request.Emisor.FechaFinTransporte,
            ["placa"] = request.Emisor.Placa,
        };
        AddOptional(emisor, "nombreComercial", request.Emisor.NombreComercial);
        AddOptional(emisor, "rise", request.Emisor.Rise);
        AddOptional(emisor, "contribuyenteEspecial", request.Emisor.ContribuyenteEspecial);

        // Sustento a nivel de documento → BuyerInfo (GuiaRemisionXmlBuilder lee desde buyer).
        var buyer = new Dictionary<string, string>
        {
            ["codDocSustento"] = request.Sustento.CodDocSustento,
            ["numDocSustento"] = request.Sustento.NumDocSustento,
        };

        return new CreateDocumentRequest(
            DocumentType.GuiaRemision,
            emisor,
            buyer,
            Detalles: [],
            Destinatarios: request.Destinatarios);
    }

    public static CreateDocumentRequest ToCreateDocumentRequest(this CreateComprobanteRetencionRequest request)
    {
        var emisor = new Dictionary<string, string>
        {
            ["ruc"] = request.Emisor.Ruc,
            ["razonSocial"] = request.Emisor.RazonSocial,
            ["dirMatriz"] = request.Emisor.DirMatriz,
            ["estab"] = request.Emisor.Estab,
            ["ptoEmi"] = request.Emisor.PtoEmi,
            ["secuencial"] = request.Emisor.Secuencial,
            ["periodoFiscal"] = request.Emisor.PeriodoFiscal,
        };
        AddOptional(emisor, "nombreComercial", request.Emisor.NombreComercial);
        AddOptional(emisor, "obligadoContabilidad", request.Emisor.ObligadoContabilidad);

        var detalles = request.Detalles
            .Select(i => new DocumentItemDto(
                i.CodigoPrincipal,
                i.Descripcion,
                i.Cantidad,
                i.PrecioUnitario,
                i.Descuento,
                i.CodigoImpuesto,
                i.CodigoPorcentaje,
                i.CodigoAuxiliar,
                i.TipoDocSustento,
                i.NumDocSustento,
                i.FechaEmisionDocSustento,
                i.NumAutDocSustento))
            .ToList();

        return new CreateDocumentRequest(
            DocumentType.ComprobanteRetencion,
            emisor,
            BuildComprador(request.SujetoRetenido),
            detalles);
    }

    private static Dictionary<string, string> BuildEmisorBase(EmisorBaseDto emisor)
    {
        var dict = new Dictionary<string, string>
        {
            ["ruc"] = emisor.Ruc,
            ["razonSocial"] = emisor.RazonSocial,
            ["dirMatriz"] = emisor.DirMatriz,
            ["estab"] = emisor.Estab,
            ["ptoEmi"] = emisor.PtoEmi,
            ["secuencial"] = emisor.Secuencial,
        };
        AddOptional(dict, "nombreComercial", emisor.NombreComercial);
        AddOptional(dict, "obligadoContabilidad", emisor.ObligadoContabilidad);
        return dict;
    }

    private static Dictionary<string, string> BuildComprador(CompradorDto comprador)
    {
        var dict = new Dictionary<string, string>
        {
            ["tipoIdentificacion"] = comprador.TipoIdentificacion,
            ["razonSocial"] = comprador.RazonSocial,
            ["identificacion"] = comprador.Identificacion,
        };
        AddOptional(dict, "email", comprador.Email);
        AddOptional(dict, "direccion", comprador.Direccion);
        AddOptional(dict, "telefono", comprador.Telefono);
        return dict;
    }

    private static List<DocumentItemDto> MapItems(IEnumerable<ItemDto> items) =>
        items.Select(i => new DocumentItemDto(
            i.CodigoPrincipal,
            i.Descripcion,
            i.Cantidad,
            i.PrecioUnitario,
            i.Descuento,
            i.CodigoImpuesto,
            i.CodigoPorcentaje,
            i.CodigoAuxiliar)).ToList();

    private static void AddOptional(Dictionary<string, string> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value;
        }
    }

    private static string FormatDate(DateTime date) =>
        date.ToString(SriDateFormat, CultureInfo.InvariantCulture);
}
