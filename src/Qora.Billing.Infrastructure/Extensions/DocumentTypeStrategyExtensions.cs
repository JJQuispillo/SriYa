using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Extensions;

/// <summary>
/// Métodos de extensión para resolver instancias de <see cref="IDocumentTypeStrategy"/>
/// a partir de una colección de estrategias registradas.
/// </summary>
public static class DocumentTypeStrategyExtensions
{
    /// <summary>
    /// Resuelve la estrategia registrada para el <paramref name="documentType"/> dado.
    /// </summary>
    /// <param name="strategies">La colección de todas las estrategias registradas.</param>
    /// <param name="documentType">El tipo de documento para el que se resuelve una estrategia.</param>
    /// <returns>El <see cref="IDocumentTypeStrategy"/> coincidente.</returns>
    /// <exception cref="InvalidOperationException">
    /// Se lanza cuando no hay ninguna estrategia registrada para el tipo de documento solicitado.
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
