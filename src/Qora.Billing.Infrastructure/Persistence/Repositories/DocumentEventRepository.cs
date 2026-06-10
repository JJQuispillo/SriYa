using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class DocumentEventRepository : IDocumentEventRepository
{
    private readonly BillingDbContext _context;

    public DocumentEventRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<DocumentEvent> CreateAsync(DocumentEvent documentEvent, CancellationToken cancellationToken = default)
    {
        await _context.DocumentEvents.AddAsync(documentEvent, cancellationToken);
        return documentEvent;
    }

    public async Task<IReadOnlyList<DocumentEvent>> GetByDocumentIdAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        return await _context.DocumentEvents
            .Where(de => de.DocumentId == documentId)
            .OrderBy(de => de.OccurredAt)
            .ToListAsync(cancellationToken);
    }
}
