using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Api.Middleware;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for tenant management.
/// POST requires ServiceToken auth (internal use only).
/// GET/PUT require ApiKey or ServiceToken auth.
/// </summary>
public static class TenantEndpoints
{
    public static RouteGroupBuilder MapTenantEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/tenants")
            .WithTags("Tenants")
            .WithOpenApi();

        group.MapPost("/", CreateTenant)
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = ServiceTokenAuthenticationHandler.SchemeName })
            .WithName("CreateTenant")
            .WithSummary("Register a new tenant (service-to-service only)");

        group.MapGet("/{id:guid}", GetTenantById)
            .RequireAuthorization()
            .WithName("GetTenantById")
            .WithSummary("Get tenant details by ID");

        group.MapPut("/{id:guid}", UpdateTenant)
            .RequireAuthorization()
            .WithName("UpdateTenant")
            .WithSummary("Update tenant details");

        return group;
    }

    private static async Task<Created<TenantResponse>> CreateTenant(
        [FromBody] CreateTenantRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateTenantCommand(request), ct);
        return TypedResults.Created($"/api/v1/tenants/{result.Id}", result);
    }

    private static async Task<Results<Ok<TenantResponse>, NotFound>> GetTenantById(
        Guid id,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetTenantByIdQuery(id), ct);
        return result is not null
            ? TypedResults.Ok(result)
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<TenantResponse>, NotFound>> UpdateTenant(
        Guid id,
        [FromBody] UpdateTenantRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateTenantCommand(id, request), ct);
        return TypedResults.Ok(result);
    }
}
