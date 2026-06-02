namespace Qora.Billing.Application.DTOs.Requests;

/// <summary>
/// Solicitud para crear una Factura (01).
/// </summary>
public record CreateFacturaRequest(
    EmisorBaseDto Emisor,
    CompradorDto Comprador,
    List<ItemDto> Detalles);

/// <summary>
/// Solicitud para crear una Liquidación de Compra (03).
/// El triplete de retención es opcional (todo-o-nada).
/// </summary>
public record CreateLiquidacionCompraRequest(
    EmisorBaseDto Emisor,
    ProveedorDto Proveedor,
    List<ItemDto> Detalles,
    RetencionEmisorDto? Retencion = null);

/// <summary>
/// Solicitud para crear una Nota de Crédito (04).
/// </summary>
public record CreateNotaCreditoRequest(
    EmisorBaseDto Emisor,
    CompradorDto Comprador,
    DocSustentoModificacionDto Sustento,
    List<ItemDto> Detalles);

/// <summary>
/// Solicitud para crear una Nota de Débito (05).
/// </summary>
public record CreateNotaDebitoRequest(
    EmisorBaseDto Emisor,
    CompradorDto Comprador,
    DocSustentoDto Sustento,
    List<ItemDto> Detalles);

/// <summary>
/// Solicitud para crear una Guía de Remisión (06). Contrato solo-destinatarios.
/// </summary>
public record CreateGuiaRemisionRequest(
    EmisorGuiaDto Emisor,
    GuiaSustentoDto Sustento,
    List<DestinatarioDto> Destinatarios);

/// <summary>
/// Solicitud para crear un Comprobante de Retención (07).
/// </summary>
public record CreateComprobanteRetencionRequest(
    EmisorRetencionDto Emisor,
    CompradorDto SujetoRetenido,
    List<RetencionItemDto> Detalles);
