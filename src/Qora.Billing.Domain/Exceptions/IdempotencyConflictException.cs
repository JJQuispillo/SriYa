namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Se lanza cuando una misma Idempotency-Key se reusa con un cuerpo de request distinto (request_hash
/// diferente). Hereda de <see cref="DocumentValidationException"/> para que el GlobalExceptionHandler la
/// mapee a 422 Unprocessable Entity (reuso de clave con payload distinto).
/// </summary>
public class IdempotencyConflictException : DocumentValidationException
{
    public IdempotencyConflictException(string message) : base(message)
    {
    }
}
