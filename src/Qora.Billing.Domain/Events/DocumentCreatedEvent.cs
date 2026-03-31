namespace Qora.Billing.Domain.Events;

public sealed class DocumentCreatedEvent : DomainEvent
{
    public override string EventType => "DocumentCreated";
    public Guid DocumentId { get; }
    public Guid TenantId { get; }
    public Enums.DocumentType DocumentType { get; }

    public DocumentCreatedEvent(Guid documentId, Guid tenantId, Enums.DocumentType documentType)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        DocumentType = documentType;
    }
}
