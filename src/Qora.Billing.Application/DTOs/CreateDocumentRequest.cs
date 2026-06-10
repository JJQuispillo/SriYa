using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs;

/// <summary>
/// Contrato interno (DTO de handler) consumido por <c>ProcessDocumentCommand</c>.
/// Ya NO es el cuerpo HTTP de ningún endpoint: los endpoints exponen requests
/// tipados por tipo de documento y los mapean a este DTO mediante
/// <c>DocumentRequestMappers</c>. Sigue siendo basado en diccionarios porque
/// IssuerInfo/BuyerInfo se persisten como jsonb y los XML builders los leen así.
/// </summary>
public record CreateDocumentRequest(
    DocumentType TipoDocumento,
    Dictionary<string, string> Emisor,
    Dictionary<string, string> Comprador,
    List<DocumentItemDto> Detalles,
    List<DestinatarioDto>? Destinatarios = null);

public record DocumentItemDto(
    string CodigoPrincipal,
    string Descripcion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    string CodigoImpuesto,
    string CodigoPorcentaje,
    string? CodigoAuxiliar = null,
    string? TipoDocSustento = null,
    string? NumDocSustento = null,
    DateTime? FechaEmisionDocSustento = null,
    string? NumAutDocSustento = null);
