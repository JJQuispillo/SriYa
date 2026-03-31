using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface IElectronicSignatureRepository
{
    Task<ElectronicSignature?> GetActiveByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<ElectronicSignature> CreateAsync(ElectronicSignature signature, CancellationToken cancellationToken = default);
    Task UpdateAsync(ElectronicSignature signature, CancellationToken cancellationToken = default);
}
