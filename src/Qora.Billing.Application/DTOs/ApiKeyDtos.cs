namespace Qora.Billing.Application.DTOs;

public record CreateApiKeyRequest(
    string Name,
    DateTime? ExpiresAt = null);

public record ApiKeyResponse(
    Guid Id,
    string Name,
    string? Key,
    bool IsActive,
    DateTime? ExpiresAt,
    DateTime CreatedAt);
