namespace Qora.Billing.Application.DTOs;

/// <summary>
/// Represents a destinatario (recipient) in a GuiaRemision document request.
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
/// Represents a transported item within a GuiaRemision destinatario.
/// </summary>
public record DestinatarioItemDto(
    string CodigoInterno,
    string DescripcionDetalle,
    decimal CantidadDetalle);
