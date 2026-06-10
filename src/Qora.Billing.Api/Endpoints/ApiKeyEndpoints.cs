using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Endpoints de Minimal API para la gestión de API keys.
/// Todos los endpoints requieren autenticación y contexto de tenant.
/// </summary>
public static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/api-keys")
            .RequireAuthorization()
            .WithTags("API Keys")
            .WithOpenApi();

        group.MapGet("/", GetApiKeys)
            .WithName("GetApiKeys")
            .WithSummary("Get paginated list of API keys for the current tenant");

        group.MapPost("/", CreateApiKey)
            .WithName("CreateApiKey")
            .WithSummary("Generate a new API key for the current tenant");

        group.MapDelete("/{id:guid}", RevokeApiKey)
            .WithName("RevokeApiKey")
            .WithSummary("Revoke an existing API key");

        return group;
    }

    private static async Task<Ok<PaginatedResponse<ApiKeyResponse>>> GetApiKeys(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var query = new GetApiKeysByTenantQuery(tenantId, page > 0 ? page : 1, pageSize > 0 ? pageSize : 20);
        var result = await sender.Send(query, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Created<ApiKeyResponse>> CreateApiKey(
        [FromBody] CreateApiKeyRequest request,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new CreateApiKeyCommand(tenantId, request), ct);
        return TypedResults.Created($"/api/v1/api-keys/{result.Id}", result);
    }

    private static async Task<Results<Ok, NotFound>> RevokeApiKey(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new RevokeApiKeyCommand(tenantId, id), ct);
        return result
            ? TypedResults.Ok()
            : TypedResults.NotFound();
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("El contexto del tenant no está configurado.");
    }
}
