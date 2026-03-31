using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Domain.Entities;

public class Tenant : BaseEntity
{
    public Ruc Ruc { get; private set; } = null!;
    public string BusinessName { get; private set; } = string.Empty;
    public string? TradeName { get; private set; }
    public bool IsActive { get; private set; }

    private Tenant() { } // EF Core

    public static Tenant Create(string ruc, string businessName, string? tradeName = null)
    {
        var tenant = new Tenant
        {
            Ruc = new Ruc(ruc),
            BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName)),
            TradeName = tradeName,
            IsActive = true
        };

        return tenant;
    }

    public void Update(string businessName, string? tradeName)
    {
        BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName));
        TradeName = tradeName;
        SetUpdatedAt();
    }

    public void Deactivate()
    {
        IsActive = false;
        SetUpdatedAt();
    }

    public void Activate()
    {
        IsActive = true;
        SetUpdatedAt();
    }

    public void EnsureActive()
    {
        if (!IsActive)
            throw new TenantInactiveException(Id);
    }
}
