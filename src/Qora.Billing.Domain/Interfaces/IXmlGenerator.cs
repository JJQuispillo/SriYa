using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Genera la representación XML sin firmar de un documento de facturación.
/// </summary>
public interface IXmlGenerator
{
    Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default);
}
