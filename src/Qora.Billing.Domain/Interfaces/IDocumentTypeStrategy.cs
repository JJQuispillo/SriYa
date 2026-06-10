using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Resultado estructural de <see cref="IDocumentTypeStrategy.BuildXmlAsync"/>: el XML generado y la
/// <see cref="AccessKey"/> (claveAcceso de 49 dígitos) que la estrategia incrustó en él. Devolver la
/// clave de forma estructural evita que el handler la re-parsee del string del XML.
/// </summary>
public sealed record BuildXmlResult(string Xml, AccessKey AccessKey);

/// <summary>
/// Interfaz de estrategia para el comportamiento específico de cada tipo de documento (Factura, NotaCredito, etc.).
/// Cada tipo de documento la implementa para proveer su propia estructura XML, validación y diseño del RIDE.
/// </summary>
public interface IDocumentTypeStrategy
{
    Enums.DocumentType DocumentType { get; }
    Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document, CancellationToken cancellationToken = default);
    Task<BuildXmlResult> BuildXmlAsync(Document document, CancellationToken cancellationToken = default);
    Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default);
}
