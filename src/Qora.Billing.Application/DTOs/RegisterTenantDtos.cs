namespace Qora.Billing.Application.DTOs;

public record RegisterTenantRequest(
    string Ruc,
    string BusinessName,
    string? TradeName,
    string ContactEmail);

public record RegisterTenantResponse(
    Guid TenantId,
    string ApiKey,
    DateTime TrialEndsAt,
    string Message);
