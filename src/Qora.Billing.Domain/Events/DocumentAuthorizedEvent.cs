namespace Qora.Billing.Domain.Events;

public sealed class DocumentAuthorizedEvent : DomainEvent
{
    public override string EventType => "DocumentAuthorized";
    public Guid DocumentId { get; }
    public string AuthorizationNumber { get; }
    public DateTime AuthorizationDate { get; }

    public DocumentAuthorizedEvent(Guid documentId, string authorizationNumber, DateTime authorizationDate)
    {
        DocumentId = documentId;
        AuthorizationNumber = authorizationNumber;
        AuthorizationDate = authorizationDate;
    }
}
