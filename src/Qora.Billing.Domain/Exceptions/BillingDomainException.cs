namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Base exception for all billing domain errors.
/// </summary>
public class BillingDomainException : Exception
{
    public BillingDomainException(string message) : base(message) { }
    public BillingDomainException(string message, Exception innerException) : base(message, innerException) { }
}
