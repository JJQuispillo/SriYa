using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface IDocumentEventRepository
{
    Task<DocumentEvent> CreateAsync(DocumentEvent documentEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentEvent>> GetByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default);
}
