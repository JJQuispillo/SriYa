namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Representa un ítem transportado (detalle) dentro de un destinatario de GuiaRemision.
/// Intencionalmente reducido — sin campos de impuestos (no aplican a documentos de transporte).
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
            throw new ArgumentException("La cantidad detalle debe ser mayor a cero.", nameof(cantidadDetalle));

        return new DestinatarioItem
        {
            CodigoInterno = codigoInterno ?? throw new ArgumentNullException(nameof(codigoInterno)),
            DescripcionDetalle = descripcionDetalle ?? throw new ArgumentNullException(nameof(descripcionDetalle)),
            CantidadDetalle = cantidadDetalle
        };
    }
}
