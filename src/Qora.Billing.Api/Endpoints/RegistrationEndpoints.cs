using MediatR;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for tenant self-registration.
/// Anonymous — no API key required.
/// </summary>
public static class RegistrationEndpoints
{
    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1")
            .WithTags("Registro")
            .WithOpenApi();

        group.MapPost("/register", async (RegisterTenantRequest request, ISender sender, CancellationToken ct) =>
        {
            var command = new RegisterTenantCommand(request.Ruc, request.BusinessName, request.TradeName, request.ContactEmail);
            var result = await sender.Send(command, ct);
            return Results.Ok(result);
        })
        .RequireRateLimiting("api-key-policy")
        .AllowAnonymous()
        .WithSummary("Registrar nueva empresa");

        return routes;
    }
}
