namespace Qora.Billing.Domain.Enums;

public enum EventType
{
    Created,
    XmlGenerated,
    Signed,
    SentToSri,
    Authorized,
    Rejected,
    RetryScheduled,
    RetryAttempted,
    Voided,
    PdfGenerated,
    Failed
}
