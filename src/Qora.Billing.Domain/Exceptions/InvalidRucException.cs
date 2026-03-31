namespace Qora.Billing.Domain.Exceptions;

public class InvalidRucException : BillingDomainException
{
    public InvalidRucException(string message) : base(message) { }
}
