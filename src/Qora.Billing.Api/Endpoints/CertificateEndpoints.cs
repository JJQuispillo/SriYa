using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Queries;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for digital certificate management.
/// All endpoints require authentication and tenant context.
/// </summary>
public static class CertificateEndpoints
{
    private static readonly string[] AllowedExtensions = [".p12", ".pfx"];
    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public static RouteGroupBuilder MapCertificateEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/certificates")
            .RequireAuthorization()
            .WithTags("Certificates")
            .WithOpenApi();

        group.MapPost("/", UploadCertificate)
            .WithName("UploadCertificate")
            .WithSummary("Upload a digital certificate (.p12/.pfx) for signing documents")
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data");

        group.MapGet("/", GetCertificates)
            .WithName("GetCertificates")
            .WithSummary("Get all certificates for the current tenant");

        return group;
    }

    private static async Task<Results<Created<CertificateResponse>, BadRequest<string>>> UploadCertificate(
        IFormFile certificate,
        [FromForm] string password,
        [FromForm] string ownerName,
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        // Validate file presence
        if (certificate is null || certificate.Length == 0)
            return TypedResults.BadRequest("El archivo del certificado es requerido.");

        // Validate file extension
        var extension = Path.GetExtension(certificate.FileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            return TypedResults.BadRequest("Solo se aceptan archivos .p12 y .pfx.");

        // Validate file size
        if (certificate.Length > MaxFileSizeBytes)
            return TypedResults.BadRequest($"El archivo del certificado no debe exceder los {MaxFileSizeBytes / (1024 * 1024)} MB.");

        // Read file bytes
        using var memoryStream = new MemoryStream();
        await certificate.CopyToAsync(memoryStream, ct);
        var certificateData = memoryStream.ToArray();

        var tenantId = GetRequiredTenantId(tenantContext);
        var command = new UploadCertificateCommand(tenantId, certificateData, password, ownerName);
        var result = await sender.Send(command, ct);
        return TypedResults.Created($"/api/v1/certificates/{result.Id}", result);
    }

    private static async Task<Ok<List<CertificateResponse>>> GetCertificates(
        ITenantContext tenantContext,
        ISender sender,
        CancellationToken ct)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var result = await sender.Send(new GetCertificatesQuery(tenantId), ct);
        return TypedResults.Ok(result);
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException("El contexto del tenant no está configurado.");
    }
}
