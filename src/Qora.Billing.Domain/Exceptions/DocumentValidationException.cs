namespace Qora.Billing.Domain.Exceptions;

public class DocumentValidationException : BillingDomainException
{
    public IReadOnlyList<string> Errors { get; }

    public DocumentValidationException(string message) : base(message)
    {
        Errors = [message];
    }

    public DocumentValidationException(IEnumerable<string> errors)
        : base($"La validación del documento falló: {string.Join("; ", errors)}")
    {
        Errors = errors.ToList().AsReadOnly();
    }
}
