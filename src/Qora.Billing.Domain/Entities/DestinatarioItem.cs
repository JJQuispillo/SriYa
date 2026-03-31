namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Represents a transported item (detalle) within a GuiaRemision destinatario.
/// Intentionally slim — no tax fields (not applicable for transport documents).
/// </summary>
public class DestinatarioItem : BaseEntity
{
    public Guid DestinatarioId { get; internal set; }

    public string CodigoInterno { get; private set; } = string.Empty;
    public string DescripcionDetalle { get; private set; } = string.Empty;
    public decimal CantidadDetalle { get; private set; }

    private DestinatarioItem() { } // EF Core

    public static DestinatarioItem Create(
        string codigoInterno,
        string descripcionDetalle,
        decimal cantidadDetalle)
    {
        if (cantidadDetalle <= 0)
            throw new ArgumentException("CantidadDetalle must be greater than zero.", nameof(cantidadDetalle));

        return new DestinatarioItem
        {
            CodigoInterno = codigoInterno ?? throw new ArgumentNullException(nameof(codigoInterno)),
            DescripcionDetalle = descripcionDetalle ?? throw new ArgumentNullException(nameof(descripcionDetalle)),
            CantidadDetalle = cantidadDetalle
        };
    }
}
