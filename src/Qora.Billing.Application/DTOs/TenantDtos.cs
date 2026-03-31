namespace Qora.Billing.Application.DTOs;

public record CreateTenantRequest(
    string Ruc,
    string BusinessName,
    string? TradeName = null);

public record UpdateTenantRequest(
    string BusinessName,
    string? TradeName = null,
    bool? IsActive = null);

public record TenantResponse(
    Guid Id,
    string Ruc,
    string BusinessName,
    string? TradeName,
    bool IsActive,
    DateTime CreatedAt);
