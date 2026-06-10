using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Domain.Entities;

public class ElectronicSignature : BaseEntity
{
    public Guid TenantId { get; private set; }
    public byte[] CertificateData { get; private set; } = [];
    public string PasswordEncrypted { get; private set; } = string.Empty;
    public string OwnerName { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public bool IsActive { get; private set; }

    private ElectronicSignature() { } // EF Core

    public static ElectronicSignature Create(
        Guid tenantId,
        byte[] certificateData,
        string passwordEncrypted,
        string ownerName,
        DateTime expiresAt)
    {
        return new ElectronicSignature
        {
            TenantId = tenantId,
            CertificateData = certificateData ?? throw new ArgumentNullException(nameof(certificateData)),
            PasswordEncrypted = passwordEncrypted ?? throw new ArgumentNullException(nameof(passwordEncrypted)),
            OwnerName = ownerName ?? throw new ArgumentNullException(nameof(ownerName)),
            ExpiresAt = expiresAt,
            IsActive = true
        };
    }

    public void Deactivate()
    {
        IsActive = false;
        SetUpdatedAt();
    }

    public bool IsExpired() => ExpiresAt < DateTime.UtcNow;

    public void EnsureValid()
    {
        if (!IsActive)
            throw new CertificateExpiredException(TenantId, ExpiresAt);

        if (IsExpired())
            throw new CertificateExpiredException(TenantId, ExpiresAt);
    }
}
