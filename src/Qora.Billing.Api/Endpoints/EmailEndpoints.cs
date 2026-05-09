using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Application.DTOs.Email;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries.Email;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for email delivery configuration and dispatch.
/// </summary>
public static class EmailEndpoints
{
    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder routes)
    {
        // ── Document email dispatch ──────────────────────────────────────
        var docs = routes.MapGroup("/api/v1/documents")
            .RequireAuthorization()
            .WithTags("Email")
            .WithOpenApi();

        docs.MapPost("/{id:guid}/email", SendDocumentEmail)
            .WithName("SendDocumentEmail")
            .WithSummary("Send the authorized document to the buyer's email address");

        // ── Tenant email settings ────────────────────────────────────────
        var tenants = routes.MapGroup("/api/v1/tenants")
            .RequireAuthorization()
            .WithTags("Email")
            .WithOpenApi();

        tenants.MapGet("/{id:guid}/email-settings", GetEmailSettings)
            .WithName("GetEmailSettings")
            .WithSummary("Get the tenant's email delivery configuration");

        tenants.MapPut("/{id:guid}/email-settings", ConfigureEmailSettings)
            .WithName("ConfigureEmailSettings")
            .WithSummary("Update the tenant's email delivery configuration");

        tenants.MapPost("/{id:guid}/email-settings/test", TestEmailConnection)
            .WithName("TestEmailConnection")
            .WithSummary("Test the SMTP connection for the tenant");

        return routes;
    }

    private static async Task<Results<Ok<bool>, NotFound>> SendDocumentEmail(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new SendDocumentEmailCommand(id, tenantId), ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<EmailSettingsDto>, NotFound>> GetEmailSettings(
        Guid id,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetEmailSettingsQuery(id), ct);
        return result is not null
            ? TypedResults.Ok(result)
            : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> ConfigureEmailSettings(
        Guid id,
        [FromBody] ConfigureEmailSettingsRequest request,
        ISender sender,
        CancellationToken ct)
    {
        await sender.Send(new ConfigureEmailSettingsCommand(
            id,
            request.EmailEnabled,
            request.EmailProvider,
            request.SmtpHost,
            request.SmtpPort,
            request.SmtpUser,
            request.SmtpPassword,
            request.UseSsl,
            request.SenderEmail,
            request.SenderName), ct);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<bool>> TestEmailConnection(
        Guid id,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new TestEmailCommand(id), ct);
        return TypedResults.Ok(result);
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("El contexto del tenant no está configurado.");
    }
}

/// <summary>
/// Request body for configuring email settings.
/// </summary>
public record ConfigureEmailSettingsRequest(
    bool EmailEnabled,
    EmailProvider EmailProvider,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    string? SmtpPassword,
    bool UseSsl,
    string? SenderEmail,
    string? SenderName);
