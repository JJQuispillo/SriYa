using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Domain.Entities;

public class DocumentEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DocumentId { get; private set; }
    public Guid TenantId { get; private set; }
    public EventType EventType { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; } = DateTime.UtcNow;

    private DocumentEvent() { } // EF Core

    public static DocumentEvent Create(Guid documentId, Guid tenantId, EventType eventType, string description)
    {
        return new DocumentEvent
        {
            DocumentId = documentId,
            TenantId = tenantId,
            EventType = eventType,
            Description = description ?? throw new ArgumentNullException(nameof(description))
        };
    }
}
