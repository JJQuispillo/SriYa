using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for usage/metering queries.
/// </summary>
public static class UsageEndpoints
{
    public static RouteGroupBuilder MapUsageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/usage")
            .RequireAuthorization()
            .WithTags("Usage")
            .WithOpenApi();

        group.MapGet("/", GetUsage)
            .WithName("GetUsage")
            .WithSummary("Get document usage statistics for a period");

        return group;
    }

    private static async Task<Ok<UsageResponse>> GetUsage(
        [FromQuery] string? period,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new GetUsageQuery(tenantId, period), ct);
        return TypedResults.Ok(result);
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is not set.");
    }
}
