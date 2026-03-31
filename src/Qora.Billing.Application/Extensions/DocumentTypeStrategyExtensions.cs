using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Extensions;

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
    /// <exception cref="DocumentTypeNotSupportedException">
    /// Thrown when no strategy is registered for the requested document type.
    /// </exception>
    public static IDocumentTypeStrategy ResolveByDocumentType(
        this IEnumerable<IDocumentTypeStrategy> strategies,
        DocumentType documentType)
    {
        var strategy = strategies.FirstOrDefault(s => s.DocumentType == documentType);
        if (strategy is null)
            throw new DocumentTypeNotSupportedException(documentType);
        return strategy;
    }
}
