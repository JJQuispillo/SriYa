namespace Qora.Billing.Domain.Entities;

public class ApiKey : BaseEntity
{
    public Guid TenantId { get; private set; }
    public string KeyHash { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime? ExpiresAt { get; private set; }

    private ApiKey() { } // EF Core

    public static ApiKey Create(Guid tenantId, string keyHash, string name, DateTime? expiresAt = null)
    {
        return new ApiKey
        {
            TenantId = tenantId,
            KeyHash = keyHash ?? throw new ArgumentNullException(nameof(keyHash)),
            Name = name ?? throw new ArgumentNullException(nameof(name)),
            IsActive = true,
            ExpiresAt = expiresAt
        };
    }

    public void Deactivate()
    {
        IsActive = false;
        SetUpdatedAt();
    }

    public bool IsExpired() => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;

    public bool IsValid() => IsActive && !IsExpired();
}
