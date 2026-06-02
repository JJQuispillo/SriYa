using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.DTOs.Requests;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Mapping;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Endpoints de Minimal API para las operaciones de documentos electrónicos.
/// Todos los endpoints requieren autenticación ApiKey y contexto de tenant.
/// </summary>
public static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/documents")
            .RequireAuthorization()
            .WithTags("Documents")
            .WithOpenApi();

        // Endpoints tipados por tipo de documento (uno por comprobante SRI).
        group.MapPost("/facturas", CreateFactura)
            .WithName("CreateFactura")
            .WithSummary("Crear y enviar una Factura (01) al SRI");

        group.MapPost("/liquidaciones-compra", CreateLiquidacionCompra)
            .WithName("CreateLiquidacionCompra")
            .WithSummary("Crear y enviar una Liquidación de Compra (03) al SRI");

        group.MapPost("/notas-credito", CreateNotaCredito)
            .WithName("CreateNotaCredito")
            .WithSummary("Crear y enviar una Nota de Crédito (04) al SRI");

        group.MapPost("/notas-debito", CreateNotaDebito)
            .WithName("CreateNotaDebito")
            .WithSummary("Crear y enviar una Nota de Débito (05) al SRI");

        group.MapPost("/guias-remision", CreateGuiaRemision)
            .WithName("CreateGuiaRemision")
            .WithSummary("Crear y enviar una Guía de Remisión (06) al SRI");

        group.MapPost("/retenciones", CreateComprobanteRetencion)
            .WithName("CreateComprobanteRetencion")
            .WithSummary("Crear y enviar un Comprobante de Retención (07) al SRI");

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

    private static Task<Created<DocumentResponse>> CreateFactura(
        [FromBody] CreateFacturaRequest request,
        IValidator<CreateFacturaRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    private static Task<Created<DocumentResponse>> CreateLiquidacionCompra(
        [FromBody] CreateLiquidacionCompraRequest request,
        IValidator<CreateLiquidacionCompraRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    private static Task<Created<DocumentResponse>> CreateNotaCredito(
        [FromBody] CreateNotaCreditoRequest request,
        IValidator<CreateNotaCreditoRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    private static Task<Created<DocumentResponse>> CreateNotaDebito(
        [FromBody] CreateNotaDebitoRequest request,
        IValidator<CreateNotaDebitoRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    private static Task<Created<DocumentResponse>> CreateGuiaRemision(
        [FromBody] CreateGuiaRemisionRequest request,
        IValidator<CreateGuiaRemisionRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    private static Task<Created<DocumentResponse>> CreateComprobanteRetencion(
        [FromBody] CreateComprobanteRetencionRequest request,
        IValidator<CreateComprobanteRetencionRequest> validator,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct) =>
        ProcessTypedAsync(request, validator, r => r.ToCreateDocumentRequest(), tenantContext, sender, ct);

    /// <summary>
    /// Flujo común de los endpoints tipados: valida el request tipado, lo mapea al
    /// contrato interno CreateDocumentRequest y lo despacha vía el ProcessDocumentCommand compartido.
    /// Una validación fallida lanza ValidationException → GlobalExceptionHandler responde 422.
    /// </summary>
    private static async Task<Created<DocumentResponse>> ProcessTypedAsync<TRequest>(
        TRequest request,
        IValidator<TRequest> validator,
        Func<TRequest, CreateDocumentRequest> map,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var validationResult = await validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }

        var tenantId = GetRequiredTenantId(tenantContext);
        var command = new ProcessDocumentCommand(tenantId, map(request));
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
            ?? throw new UnauthorizedAccessException("El contexto del tenant no está configurado.");
    }
}

/// <summary>
/// Cuerpo de la solicitud para anular un documento.
/// </summary>
public record VoidDocumentRequest(string Reason);
