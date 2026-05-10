using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for subscription and plan management.
/// All endpoints require ApiKey authentication.
/// </summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/subscription")
            .RequireAuthorization()
            .RequireRateLimiting("api-key-policy")
            .WithTags("Suscripción")
            .WithOpenApi();

        group.MapGet("/", GetSubscription)
            .WithName("GetSubscription")
            .WithSummary("Ver plan y uso actual");

        group.MapPost("/checkout", CreateCheckoutSession)
            .WithName("CreateCheckoutSession")
            .WithSummary("Crear sesión de pago en Stripe");

        return routes;
    }

    private static async Task<Ok<SubscriptionDto>> GetSubscription(
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new GetSubscriptionQuery(tenantId), ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Ok<object>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var url = await sender.Send(new CreateCheckoutSessionCommand(tenantId, request.PlanId), ct);
        return TypedResults.Ok<object>(new { checkoutUrl = url });
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("El contexto del tenant no está configurado.");
    }
}

/// <summary>Request body for creating a Stripe checkout session.</summary>
public record CreateCheckoutSessionRequest(Guid PlanId);
