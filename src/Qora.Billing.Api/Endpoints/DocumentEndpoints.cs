using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for electronic document operations.
/// All endpoints require ApiKey authentication and tenant context.
/// </summary>
public static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/documents")
            .RequireAuthorization()
            .WithTags("Documents")
            .WithOpenApi();

        group.MapPost("/process", ProcessDocument)
            .WithName("ProcessDocument")
            .WithSummary("Process and submit an electronic document to SRI");

        group.MapGet("/{id:guid}", GetDocumentById)
            .WithName("GetDocumentById")
            .WithSummary("Get a document by its ID");

        group.MapGet("/", GetDocuments)
            .WithName("GetDocuments")
            .WithSummary("Get paginated list of documents for the current tenant");

        group.MapGet("/{id:guid}/events", GetDocumentEvents)
            .WithName("GetDocumentEvents")
            .WithSummary("Get processing events for a document");

        group.MapGet("/{id:guid}/pdf", GetDocumentPdf)
            .WithName("GetDocumentPdf")
            .WithSummary("Download the RIDE PDF for a document");

        group.MapPost("/{id:guid}/void", VoidDocument)
            .WithName("VoidDocument")
            .WithSummary("Void/anulate a document");

        group.MapGet("/{id:guid}/status", CheckDocumentStatus)
            .WithName("CheckDocumentStatus")
            .WithSummary("Check the current status of a document");

        return group;
    }

    private static async Task<Results<Created<DocumentResponse>, BadRequest<ProblemDetails>>> ProcessDocument(
        [FromBody] CreateDocumentRequest request,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var command = new ProcessDocumentCommand(tenantId, request);
        var result = await sender.Send(command, ct);
        return TypedResults.Created($"/api/v1/documents/{result.Id}", result);
    }

    private static async Task<Results<Ok<DocumentResponse>, NotFound>> GetDocumentById(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new GetDocumentByIdQuery(tenantId, id), ct);
        return result is not null
            ? TypedResults.Ok(result)
            : TypedResults.NotFound();
    }

    private static async Task<Ok<PaginatedResponse<DocumentResponse>>> GetDocuments(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var query = new GetDocumentsByTenantQuery(tenantId, page > 0 ? page : 1, pageSize > 0 ? pageSize : 20);
        var result = await sender.Send(query, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<List<DocumentEventResponse>>, NotFound>> GetDocumentEvents(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new GetDocumentEventsQuery(tenantId, id), ct);
        return result.Count > 0
            ? TypedResults.Ok(result)
            : TypedResults.NotFound();
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> GetDocumentPdf(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var pdfBytes = await sender.Send(new GetDocumentPdfQuery(tenantId, id), ct);
        return pdfBytes is not null
            ? TypedResults.File(pdfBytes, "application/pdf", $"document-{id}.pdf")
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<DocumentResponse>, NotFound>> VoidDocument(
        Guid id,
        [FromBody] VoidDocumentRequest request,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new VoidDocumentCommand(tenantId, id, request.Reason), ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<DocumentResponse>, NotFound>> CheckDocumentStatus(
        Guid id,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new CheckDocumentStatusQuery(tenantId, id), ct);
        return result is not null
            ? TypedResults.Ok(result)
            : TypedResults.NotFound();
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is not set.");
    }
}

/// <summary>
/// Request body for voiding a document.
/// </summary>
public record VoidDocumentRequest(string Reason);
