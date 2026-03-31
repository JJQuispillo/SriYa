using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetApiKeysByTenantQueryHandler
    : IRequestHandler<GetApiKeysByTenantQuery, PaginatedResponse<ApiKeyResponse>>
{
    private readonly IApiKeyRepository _apiKeyRepository;

    public GetApiKeysByTenantQueryHandler(IApiKeyRepository apiKeyRepository)
    {
        _apiKeyRepository = apiKeyRepository;
    }

    public async Task<PaginatedResponse<ApiKeyResponse>> Handle(
        GetApiKeysByTenantQuery query, CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _apiKeyRepository.GetByTenantIdAsync(
            query.TenantId, query.Page, query.PageSize, cancellationToken);

        var responses = items.Select(MapToResponse).ToList();
        var totalPages = (int)Math.Ceiling((double)totalCount / query.PageSize);

        return new PaginatedResponse<ApiKeyResponse>(
            responses, query.Page, query.PageSize, totalCount, totalPages);
    }

    private static ApiKeyResponse MapToResponse(ApiKey apiKey)
    {
        // Keys are stored as hashes — never return the actual key value.
        // The key field is set to null for list responses.
        return new ApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            null,
            apiKey.IsActive,
            apiKey.ExpiresAt,
            apiKey.CreatedAt);
    }
}
