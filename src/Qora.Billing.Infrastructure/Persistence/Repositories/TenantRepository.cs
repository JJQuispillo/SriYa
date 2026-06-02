using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class TenantRepository : ITenantRepository
{
    private readonly BillingDbContext _context;

    public TenantRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Tenants
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<Tenant?> GetByRucAsync(string ruc, CancellationToken cancellationToken = default)
    {
        // Comparar usando el value object Ruc para que el value converter de EF Core pueda traducir la consulta
        var rucVO = new Ruc(ruc);
        return await _context.Tenants
            .FirstOrDefaultAsync(t => t.Ruc == rucVO, cancellationToken);
    }

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        await _context.Tenants.AddAsync(tenant, cancellationToken);
        return tenant;
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        _context.Tenants.Update(tenant);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenant = await _context.Tenants
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tenant is not null)
        {
            _context.Tenants.Remove(tenant);
        }
    }
}
