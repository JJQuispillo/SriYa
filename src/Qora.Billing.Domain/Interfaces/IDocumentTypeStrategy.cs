using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Interfaz de estrategia para el comportamiento específico de cada tipo de documento (Factura, NotaCredito, etc.).
/// Cada tipo de documento la implementa para proveer su propia estructura XML, validación y diseño del RIDE.
/// </summary>
public interface IDocumentTypeStrategy
{
    Enums.DocumentType DocumentType { get; }
    Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document, CancellationToken cancellationToken = default);
    Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default);
    Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default);
}
