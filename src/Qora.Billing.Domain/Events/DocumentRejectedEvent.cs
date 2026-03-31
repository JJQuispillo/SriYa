namespace Qora.Billing.Domain.Events;

public sealed class DocumentRejectedEvent : DomainEvent
{
    public override string EventType => "DocumentRejected";
    public Guid DocumentId { get; }
    public string Reason { get; }

    public DocumentRejectedEvent(Guid documentId, string reason)
    {
        DocumentId = documentId;
        Reason = reason;
    }
}
