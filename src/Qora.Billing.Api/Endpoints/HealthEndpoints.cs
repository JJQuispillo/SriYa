using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Health check endpoints for liveness and readiness probes.
/// No authentication required.
/// </summary>
public static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/health")
            .AllowAnonymous()
            .WithTags("Health")
            .WithOpenApi();

        group.MapGet("/", GetHealth)
            .WithName("HealthCheck")
            .WithSummary("Basic liveness check");

        group.MapGet("/ready", GetReadiness)
            .WithName("ReadinessCheck")
            .WithSummary("Readiness check including database connectivity");

        return group;
    }

    private static Ok<HealthResponse> GetHealth()
    {
        return TypedResults.Ok(new HealthResponse("Healthy", DateTime.UtcNow));
    }

    private static async Task<Results<Ok<HealthResponse>, StatusCodeHttpResult>> GetReadiness(
        BillingDbContext dbContext,
        CancellationToken ct)
    {
        try
        {
            await dbContext.Database.CanConnectAsync(ct);
            return TypedResults.Ok(new HealthResponse("Ready", DateTime.UtcNow));
        }
        catch
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}

public record HealthResponse(string Status, DateTime Timestamp);
