namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Se lanza en modo AUTO cuando el reintento acotado de asignación de secuencial server-side agota su
/// presupuesto (máx. 5) por choques repetidos contra el unique constraint de identidad de negocio
/// (ux_documents_business_identity) bajo contención patológica de la misma identidad
/// (tenant/tipo/estab/ptoEmi). Es una condición transitoria/reintentable por el cliente, por lo que el
/// handler la mapea a 409 Conflict (distinta del 422 de entrada inválida y del 500 de un bug).
/// </summary>
public class SecuencialExhaustedException : BillingDomainException
{
    public SecuencialExhaustedException(string message) : base(message)
    {
    }

    public SecuencialExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
