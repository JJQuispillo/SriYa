namespace Qora.Billing.Domain.Exceptions;

public class InvalidAccessKeyException : BillingDomainException
{
    public InvalidAccessKeyException(string message) : base(message) { }
}
