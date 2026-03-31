using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class UsageRecordRepository : IUsageRecordRepository
{
    private readonly BillingDbContext _context;

    public UsageRecordRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<UsageRecord> CreateAsync(UsageRecord usageRecord, CancellationToken cancellationToken = default)
    {
        await _context.UsageRecords.AddAsync(usageRecord, cancellationToken);
        return usageRecord;
    }

    public async Task<IReadOnlyList<UsageRecord>> GetByTenantAndPeriodAsync(
        Guid tenantId, string billingPeriod, CancellationToken cancellationToken = default)
    {
        return await _context.UsageRecords
            .Where(u => u.TenantId == tenantId && u.BillingPeriod == billingPeriod)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountByTenantAndPeriodAsync(
        Guid tenantId, string billingPeriod, CancellationToken cancellationToken = default)
    {
        return await _context.UsageRecords
            .Where(u => u.TenantId == tenantId && u.BillingPeriod == billingPeriod)
            .CountAsync(cancellationToken);
    }
}
