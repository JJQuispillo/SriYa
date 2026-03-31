using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class ElectronicSignatureRepository : IElectronicSignatureRepository
{
    private readonly BillingDbContext _context;

    public ElectronicSignatureRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<ElectronicSignature?> GetActiveByTenantIdAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.ElectronicSignatures
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ElectronicSignature> CreateAsync(
        ElectronicSignature signature, CancellationToken cancellationToken = default)
    {
        await _context.ElectronicSignatures.AddAsync(signature, cancellationToken);
        return signature;
    }

    public Task UpdateAsync(ElectronicSignature signature, CancellationToken cancellationToken = default)
    {
        _context.ElectronicSignatures.Update(signature);
        return Task.CompletedTask;
    }
}
