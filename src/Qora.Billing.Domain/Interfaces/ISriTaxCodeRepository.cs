using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface ISriTaxCodeRepository
{
    Task<SriTaxCode?> FindAsync(string taxTypeCode, string percentageCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SriTaxCode>> GetAllActiveAsync(CancellationToken cancellationToken = default);
}
