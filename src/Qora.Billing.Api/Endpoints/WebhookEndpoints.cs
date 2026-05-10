using MediatR;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for third-party webhook ingestion.
/// Anonymous — HMAC signature verification is performed inside the command handler.
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/webhooks/stripe", async (HttpContext ctx, ISender sender, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("WebhookEndpoints");
            // Enable buffering so the body can be read (raw bytes needed for HMAC verification)
            ctx.Request.EnableBuffering();

            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var payload = await reader.ReadToEndAsync();
            var signature = ctx.Request.Headers["Stripe-Signature"].FirstOrDefault() ?? string.Empty;

            try
            {
                await sender.Send(new HandleStripeWebhookCommand(payload, signature));
                return Results.Ok();
            }
            catch (Exception ex) when (ex.Message.Contains("signature", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("HMAC", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Stripe webhook HMAC inválido: {Message}", ex.Message);
                return Results.BadRequest("Firma inválida");
            }
        })
        .AllowAnonymous()
        .WithTags("Webhooks")
        .WithOpenApi()
        .WithSummary("Webhook de Stripe");

        return routes;
    }
}
