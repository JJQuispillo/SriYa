using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs;

public record DocumentResponse(
    Guid Id,
    Guid TenantId,
    DocumentType DocumentType,
    string? AccessKey,
    DocumentStatus Status,
    string? AuthorizationNumber,
    DateTime? AuthorizationDate,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? ProcessedAt);

public record DocumentEventResponse(
    EventType EventType,
    string Description,
    DateTime OccurredAt);
