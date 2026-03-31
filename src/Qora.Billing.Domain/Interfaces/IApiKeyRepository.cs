using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface IApiKeyRepository
{
    Task<ApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKey>> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ApiKey> Items, int TotalCount)> GetByTenantIdAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default);
    Task UpdateAsync(ApiKey apiKey, CancellationToken cancellationToken = default);
}
