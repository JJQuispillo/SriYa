namespace Qora.Billing.Application.DTOs.Requests;

/// <summary>
/// Emisor común a los 6 tipos de documento. Solo contiene los campos que provee el
/// llamador (RequiredIssuerFields). Los campos generados por el sistema
/// (ambiente, tipoEmision, claveAcceso, fechaEmision) NUNCA aparecen aquí.
/// </summary>
public record EmisorBaseDto(
    string Ruc,
    string RazonSocial,
    string DirMatriz,
    string Estab,
    string PtoEmi,
    string? Secuencial,
    string? NombreComercial = null,
    string? ObligadoContabilidad = null);

/// <summary>
/// Comprador estándar (Factura / NotaCredito / NotaDebito / Retencion).
/// </summary>
public record CompradorDto(
    string TipoIdentificacion,
    string RazonSocial,
    string Identificacion,
    string? Email = null,
    string? Direccion = null,
    string? Telefono = null);

/// <summary>
/// Ítem común (Factura / LiquidacionCompra / NotaCredito / NotaDebito).
/// Los campos de sustento por ítem (retención) viven en <see cref="RetencionItemDto"/>.
/// </summary>
public record ItemDto(
    string CodigoPrincipal,
    string Descripcion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    string CodigoImpuesto,
    string CodigoPorcentaje,
    string? CodigoAuxiliar = null);

/// <summary>
/// Ítem de Comprobante de Retención: agrega los campos de sustento por ítem.
/// </summary>
public record RetencionItemDto(
    string CodigoPrincipal,
    string Descripcion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    string CodigoImpuesto,
    string CodigoPorcentaje,
    string TipoDocSustento,
    string NumDocSustento,
    DateTime FechaEmisionDocSustento,
    string NumAutDocSustento,
    string? CodigoAuxiliar = null);

/// <summary>
/// Proveedor de una Liquidación de Compra (cumple el rol de "comprador" en BuyerInfo).
/// </summary>
public record ProveedorDto(
    string TipoIdentificacionProveedor,
    string RazonSocialProveedor,
    string IdentificacionProveedor,
    string DireccionProveedor);

/// <summary>
/// Triplete de retención opcional de una Liquidación de Compra (regla todo-o-nada).
/// Se mapea a IssuerInfo.
/// </summary>
public record RetencionEmisorDto(
    string CodigoRetencion,
    string PorcentajeRetencion,
    string ValorRetencion);

/// <summary>
/// Documento de sustento de una Nota de Crédito (documento modificado).
/// Se mapea a IssuerInfo bajo las claves que lee NotaCreditoXmlBuilder.
/// </summary>
public record DocSustentoModificacionDto(
    string CodDocModificado,
    string NumDocModificado,
    DateTime FechaEmisionDocSustento,
    string RazonModificacion);

/// <summary>
/// Documento de sustento de una Nota de Débito.
/// Se mapea a IssuerInfo bajo las claves que lee NotaDebitoXmlBuilder.
/// </summary>
public record DocSustentoDto(
    string CodDocSustento,
    string NumDocSustento,
    DateTime FechaEmisionDocSustento);

/// <summary>
/// Documento de sustento a nivel de documento para una Guía de Remisión.
/// Se mapea a BuyerInfo bajo las claves que lee GuiaRemisionXmlBuilder.
/// </summary>
public record GuiaSustentoDto(
    string CodDocSustento,
    string NumDocSustento);

/// <summary>
/// Emisor de una Guía de Remisión: emisor base + datos obligatorios del transportista.
/// </summary>
public record EmisorGuiaDto(
    string Ruc,
    string RazonSocial,
    string DirMatriz,
    string Estab,
    string PtoEmi,
    string? Secuencial,
    string RazonSocialTransportista,
    string RucTransportista,
    string ObligadoContabilidad,
    string FechaInicioTransporte,
    string FechaFinTransporte,
    string Placa,
    string? NombreComercial = null,
    string? Rise = null,
    string? ContribuyenteEspecial = null);

/// <summary>
/// Emisor de un Comprobante de Retención: emisor base + periodoFiscal (MM/YYYY).
/// </summary>
public record EmisorRetencionDto(
    string Ruc,
    string RazonSocial,
    string DirMatriz,
    string Estab,
    string PtoEmi,
    string? Secuencial,
    string PeriodoFiscal,
    string? NombreComercial = null,
    string? ObligadoContabilidad = null);
