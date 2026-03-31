using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Generates RIDE (Representación Impresa del Documento Electrónico) PDF for a document.
/// </summary>
public interface IRideGenerator
{
    Task<byte[]> GeneratePdfAsync(Document document, CancellationToken cancellationToken = default);
}
