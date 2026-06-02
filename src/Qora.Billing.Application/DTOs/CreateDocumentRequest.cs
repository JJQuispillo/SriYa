using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs;

public record CreateDocumentRequest(
    DocumentType TipoDocumento,
    Dictionary<string, string> Emisor,
    Dictionary<string, string> Comprador,
    List<DocumentItemDto> Detalles,
    Dictionary<string, string>? InfoAdicional = null,
    List<DestinatarioDto>? Destinatarios = null);

public record DocumentItemDto(
    string CodigoPrincipal,
    string Descripcion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    /// <summary>
    /// Ignorado — el command handler deriva la tasa de impuesto de la tabla de referencia de códigos de impuestos del SRI.
    /// Se mantiene por compatibilidad hacia atrás; cualquier valor proporcionado se descarta.
    /// </summary>
    decimal TasaImpuesto,
    string CodigoImpuesto,
    string CodigoPorcentaje,
    string? CodigoAuxiliar = null,
    string? TipoDocSustento = null,
    string? NumDocSustento = null,
    DateTime? FechaEmisionDocSustento = null,
    string? NumAutDocSustento = null);
