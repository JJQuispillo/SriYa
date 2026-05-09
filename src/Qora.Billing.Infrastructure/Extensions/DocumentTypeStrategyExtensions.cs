using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Extensions;

/// <summary>
/// Extension methods for resolving <see cref="IDocumentTypeStrategy"/> instances
/// from a collection of registered strategies.
/// </summary>
public static class DocumentTypeStrategyExtensions
{
    /// <summary>
    /// Resolves the strategy registered for the given <paramref name="documentType"/>.
    /// </summary>
    /// <param name="strategies">The collection of all registered strategies.</param>
    /// <param name="documentType">The document type to resolve a strategy for.</param>
    /// <returns>The matching <see cref="IDocumentTypeStrategy"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no strategy is registered for the requested document type.
    /// </exception>
    public static IDocumentTypeStrategy ResolveByDocumentType(
        this IEnumerable<IDocumentTypeStrategy> strategies,
        DocumentType documentType)
    {
        var strategy = strategies.FirstOrDefault(s => s.DocumentType == documentType);
        if (strategy is null)
            throw new InvalidOperationException(
                $"No hay estrategia registrada para el tipo de documento {documentType}");
        return strategy;
    }
}
