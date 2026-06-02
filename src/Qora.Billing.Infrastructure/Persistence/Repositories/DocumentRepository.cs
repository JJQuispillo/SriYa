using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class DocumentRepository : IDocumentRepository
{
    private readonly BillingDbContext _context;

    public DocumentRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<Document?> GetByAccessKeyAsync(string accessKey, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .IgnoreQueryFilters() // la búsqueda por AccessKey es entre tenants por diseño
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.AccessKey != null && d.AccessKey.Value == accessKey, cancellationToken);
    }

    public async Task<(IReadOnlyList<Document> Items, int TotalCount)> GetByTenantIdAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _context.Documents
            .Where(d => d.TenantId == tenantId)
            .OrderByDescending(d => d.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(d => d.Items)
            .ToListAsync(cancellationToken);

        return (items.AsReadOnly(), totalCount);
    }

    public async Task<IReadOnlyList<Document>> GetPendingRetryAsync(
        DateTime before, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .IgnoreQueryFilters() // el procesamiento de reintentos es entre tenants
            .Where(d => d.Status == Domain.Enums.DocumentStatus.PendingRetry
                        && d.NextRetryAt.HasValue
                        && d.NextRetryAt.Value <= before)
            .OrderBy(d => d.NextRetryAt)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    public async Task<Document> CreateAsync(Document document, CancellationToken cancellationToken = default)
    {
        await _context.Documents.AddAsync(document, cancellationToken);
        return document;
    }

    public Task UpdateAsync(Document document, CancellationToken cancellationToken = default)
    {
        _context.Documents.Update(document);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await _context.Documents
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document is not null)
        {
            _context.Documents.Remove(document);
        }
    }
}
