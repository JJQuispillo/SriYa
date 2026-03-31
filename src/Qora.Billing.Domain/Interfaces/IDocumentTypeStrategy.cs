using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Strategy interface for document-type-specific behavior (Factura, NotaCredito, etc.).
/// Each document type implements this to provide its own XML structure, validation, and RIDE layout.
/// </summary>
public interface IDocumentTypeStrategy
{
    Enums.DocumentType DocumentType { get; }
    Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document, CancellationToken cancellationToken = default);
    Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default);
    Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default);
}
