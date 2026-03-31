using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface IUsageRecordRepository
{
    Task<UsageRecord> CreateAsync(UsageRecord usageRecord, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageRecord>> GetByTenantAndPeriodAsync(
        Guid tenantId, string billingPeriod, CancellationToken cancellationToken = default);
    Task<int> CountByTenantAndPeriodAsync(
        Guid tenantId, string billingPeriod, CancellationToken cancellationToken = default);
}
