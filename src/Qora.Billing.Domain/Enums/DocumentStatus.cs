namespace Qora.Billing.Domain.Enums;

public enum DocumentStatus
{
    Draft,
    XmlGenerated,
    Signed,
    SentToSri,
    Authorized,
    Rejected,
    PendingRetry,
    Failed,
    Voided
}
