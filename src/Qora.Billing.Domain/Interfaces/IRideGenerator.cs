using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Genera el PDF del RIDE (Representación Impresa del Documento Electrónico) de un documento.
/// </summary>
public interface IRideGenerator
{
    Task<byte[]> GeneratePdfAsync(Document document, CancellationToken cancellationToken = default);
}
