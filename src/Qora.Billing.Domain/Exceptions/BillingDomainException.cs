namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Excepción base para todos los errores del dominio de facturación.
/// </summary>
public class BillingDomainException : Exception
{
    public BillingDomainException(string message) : base(message) { }
    public BillingDomainException(string message, Exception innerException) : base(message, innerException) { }
}
