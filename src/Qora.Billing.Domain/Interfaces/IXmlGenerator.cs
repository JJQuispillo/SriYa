using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Generates the unsigned XML representation of a billing document.
/// </summary>
public interface IXmlGenerator
{
    Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default);
}
