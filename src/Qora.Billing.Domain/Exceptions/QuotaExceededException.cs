namespace Qora.Billing.Domain.Exceptions;

public class QuotaExceededException : BillingDomainException
{
    public Guid TenantId { get; }
    public string PlanName { get; }
    public int Limit { get; }

    public QuotaExceededException(Guid tenantId, string planName, int limit)
        : base($"Cuota mensual de documentos excedida. El plan '{planName}' permite {limit} documentos por mes.")
    {
        TenantId = tenantId;
        PlanName = planName;
        Limit = limit;
    }
}
