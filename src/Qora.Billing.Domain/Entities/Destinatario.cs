namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Representa un destinatario en un documento GuiaRemision.
/// Cada destinatario tiene sus propios campos de identidad y una lista de ítems transportados.
/// </summary>
public class Destinatario : BaseEntity
{
    public Guid DocumentId { get; internal set; }

    public string IdentificacionDestinatario { get; private set; } = string.Empty;
    public string RazonSocialDestinatario { get; private set; } = string.Empty;
    public string DirDestinatario { get; private set; } = string.Empty;
    public string MotivoTraslado { get; private set; } = string.Empty;
    public string? RutaEntrega { get; private set; }
    public string? DocAduaneroUnico { get; private set; }
    public string? CodEstabDestino { get; private set; }
    public string RucTransportista { get; private set; } = string.Empty;
    public string? Rise { get; private set; }

    private readonly List<DestinatarioItem> _items = [];
    public IReadOnlyCollection<DestinatarioItem> Items => _items.AsReadOnly();

    private Destinatario() { } // EF Core

    public static Destinatario Create(
        string identificacionDestinatario,
        string razonSocialDestinatario,
        string dirDestinatario,
        string motivoTraslado,
        string rucTransportista,
        string? rutaEntrega = null,
        string? docAduaneroUnico = null,
        string? codEstabDestino = null,
        string? rise = null)
    {
        return new Destinatario
        {
            IdentificacionDestinatario = identificacionDestinatario ?? throw new ArgumentNullException(nameof(identificacionDestinatario)),
            RazonSocialDestinatario = razonSocialDestinatario ?? throw new ArgumentNullException(nameof(razonSocialDestinatario)),
            DirDestinatario = dirDestinatario ?? throw new ArgumentNullException(nameof(dirDestinatario)),
            MotivoTraslado = motivoTraslado ?? throw new ArgumentNullException(nameof(motivoTraslado)),
            RucTransportista = rucTransportista ?? throw new ArgumentNullException(nameof(rucTransportista)),
            RutaEntrega = rutaEntrega,
            DocAduaneroUnico = docAduaneroUnico,
            CodEstabDestino = codEstabDestino,
            Rise = rise
        };
    }

    public void AddItem(DestinatarioItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        item.DestinatarioId = Id;
        _items.Add(item);
    }
}
