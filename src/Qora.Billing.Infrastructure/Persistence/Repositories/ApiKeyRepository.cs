using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private readonly BillingDbContext _context;
    private readonly BillingPrivilegedDbContext _privilegedContext;

    public ApiKeyRepository(BillingDbContext context, BillingPrivilegedDbContext privilegedContext)
    {
        _context = context;
        _privilegedContext = privilegedContext;
    }

    public async Task<ApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        // Camino cross-tenant deliberado: la búsqueda por hash corre ANTES de conocer el tenant.
        // Usa la conexión privilegiada (BYPASSRLS) además de IgnoreQueryFilters, para que siga
        // funcionando cuando P1 active los filtros fail-closed.
        return await _privilegedContext.ApiKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.KeyHash == keyHash, cancellationToken);
    }

    public async Task<IReadOnlyList<ApiKey>> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.ApiKeys
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<ApiKey> Items, int TotalCount)> GetByTenantIdAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _context.ApiKeys
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.AsReadOnly(), totalCount);
    }

    public async Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
    {
        await _context.ApiKeys.AddAsync(apiKey, cancellationToken);
        return apiKey;
    }

    public Task UpdateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
    {
        _context.ApiKeys.Update(apiKey);
        return Task.CompletedTask;
    }
}
