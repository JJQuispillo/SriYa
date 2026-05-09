using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class SriTaxCodeRepository : ISriTaxCodeRepository
{
    private readonly BillingDbContext _context;

    public SriTaxCodeRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<SriTaxCode?> FindAsync(string taxTypeCode, string percentageCode, CancellationToken cancellationToken = default)
    {
        return await _context.SriTaxCodes
            .FirstOrDefaultAsync(t => t.TaxTypeCode == taxTypeCode && t.PercentageCode == percentageCode, cancellationToken);
    }

    public async Task<IReadOnlyList<SriTaxCode>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SriTaxCodes
            .Where(t => t.IsActive)
            .ToListAsync(cancellationToken);
    }
}
