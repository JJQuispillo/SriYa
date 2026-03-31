namespace Qora.Billing.Application.DTOs;

public record CertificateResponse(
    Guid Id,
    string OwnerName,
    DateTime ExpiresAt,
    bool IsActive,
    DateTime CreatedAt);
