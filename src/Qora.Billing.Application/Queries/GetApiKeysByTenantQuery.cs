using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetApiKeysByTenantQuery(
    Guid TenantId,
    int Page = 1,
    int PageSize = 20) : IRequest<PaginatedResponse<ApiKeyResponse>>;
