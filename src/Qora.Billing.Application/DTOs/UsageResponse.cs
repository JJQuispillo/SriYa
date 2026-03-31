namespace Qora.Billing.Application.DTOs;

public record UsageResponse(
    Guid TenantId,
    string Period,
    int DocumentCount);
