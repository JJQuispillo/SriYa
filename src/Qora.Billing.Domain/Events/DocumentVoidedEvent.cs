namespace Qora.Billing.Domain.Events;

public sealed class DocumentVoidedEvent : DomainEvent
{
    public override string EventType => "DocumentVoided";
    public Guid DocumentId { get; }
    public string Reason { get; }

    public DocumentVoidedEvent(Guid documentId, string reason)
    {
        DocumentId = documentId;
        Reason = reason;
    }
}
