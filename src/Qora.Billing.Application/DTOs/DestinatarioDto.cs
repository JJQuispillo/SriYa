namespace Qora.Billing.Application.DTOs;

/// <summary>
/// Representa un destinatario en una solicitud de documento GuiaRemision.
/// </summary>
public record DestinatarioDto(
    string IdentificacionDestinatario,
    string RazonSocialDestinatario,
    string DirDestinatario,
    string MotivoTraslado,
    string RucTransportista,
    List<DestinatarioItemDto> Items,
    string? RutaEntrega = null,
    string? DocAduaneroUnico = null,
    string? CodEstabDestino = null,
    string? Rise = null);

/// <summary>
/// Representa un ítem transportado dentro de un destinatario de GuiaRemision.
/// </summary>
public record DestinatarioItemDto(
    string CodigoInterno,
    string DescripcionDetalle,
    decimal CantidadDetalle);
