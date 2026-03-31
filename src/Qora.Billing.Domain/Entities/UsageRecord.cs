namespace Qora.Billing.Domain.Entities;

public class UsageRecord
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Enums.DocumentType DocumentType { get; private set; }
    public string BillingPeriod { get; private set; } = string.Empty; // Format: "YYYY-MM"
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private UsageRecord() { } // EF Core

    public static UsageRecord Create(
        Guid tenantId,
        Guid documentId,
        Enums.DocumentType documentType,
        string? billingPeriod = null)
    {
        return new UsageRecord
        {
            TenantId = tenantId,
            DocumentId = documentId,
            DocumentType = documentType,
            BillingPeriod = billingPeriod ?? DateTime.UtcNow.ToString("yyyy-MM")
        };
    }
}
