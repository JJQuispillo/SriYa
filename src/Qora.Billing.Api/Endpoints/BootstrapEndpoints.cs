using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Api.Middleware;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Endpoint de onboarding atómico de un emisor (OB-1).
///
/// POST /api/v1/bootstrap — SOLO autenticación ServiceToken (uso entre servicios / integrador). NO requiere
/// X-Tenant-Id: aprovisiona un emisor NUEVO, así que el tenant aún no existe al iniciar la llamada.
/// Crea de forma ATÓMICA, en una sola transacción con rollback total: el tenant (emisor), su certificado
/// .p12/.pfx (validado ANTES de persistir) y una API key inicial. La clave de API se devuelve EN CLARO una
/// ÚNICA vez en la respuesta.
///
/// Semántica de errores (mapeada por GlobalExceptionHandler):
///   - 400 BadRequest: campos obligatorios ausentes/archivo inválido (en el endpoint),
///     RUC inválido (InvalidRucException), RUC duplicado o certificado/contraseña inválidos
///     (BillingDomainException) — en todos los casos la transacción revierte y no queda nada persistido.
///   - 401 Unauthorized: sin ServiceToken válido (esquema ServiceToken).
///   - 201 Created: emisor aprovisionado; cuerpo con tenantId + apiKey en claro (una sola vez).
/// </summary>
public static class BootstrapEndpoints
{
    private static readonly string[] AllowedExtensions = [".p12", ".pfx"];
    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public static RouteGroupBuilder MapBootstrapEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/bootstrap")
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = ServiceTokenAuthenticationHandler.SchemeName
            })
            .WithTags("Bootstrap")
            .WithOpenApi();

        group.MapPost("/", Bootstrap)
            .WithName("BootstrapTenant")
            .WithSummary("Atomically onboard a new emisor: tenant + certificate + initial API key (service-to-service only)")
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data");

        return group;
    }

    private static async Task<Results<Created<BootstrapTenantResponse>, BadRequest<string>>> Bootstrap(
        IFormFile certificate,
        [FromForm] string ruc,
        [FromForm] string razonSocial,
        [FromForm] string password,
        [FromForm] string ownerName,
        [FromForm] string? nombreComercial,
        [FromForm] string? correoContacto,
        [FromForm] string? apiKeyName,
        ISender sender,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ruc))
            return TypedResults.BadRequest("El RUC es requerido.");

        if (string.IsNullOrWhiteSpace(razonSocial))
            return TypedResults.BadRequest("La razón social es requerida.");

        if (string.IsNullOrWhiteSpace(ownerName))
            return TypedResults.BadRequest("El nombre del propietario del certificado es requerido.");

        // Validación del archivo del certificado (misma política que UploadCertificate).
        if (certificate is null || certificate.Length == 0)
            return TypedResults.BadRequest("El archivo del certificado es requerido.");

        var extension = Path.GetExtension(certificate.FileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            return TypedResults.BadRequest("Solo se aceptan archivos .p12 y .pfx.");

        if (certificate.Length > MaxFileSizeBytes)
            return TypedResults.BadRequest($"El archivo del certificado no debe exceder los {MaxFileSizeBytes / (1024 * 1024)} MB.");

        using var memoryStream = new MemoryStream();
        await certificate.CopyToAsync(memoryStream, ct);
        var certificateData = memoryStream.ToArray();

        var command = new BootstrapTenantCommand(
            ruc,
            razonSocial,
            string.IsNullOrWhiteSpace(nombreComercial) ? null : nombreComercial,
            string.IsNullOrWhiteSpace(correoContacto) ? null : correoContacto,
            certificateData,
            password ?? string.Empty,
            ownerName,
            string.IsNullOrWhiteSpace(apiKeyName) ? "bootstrap" : apiKeyName);

        // Cualquier fallo (RUC inválido/duplicado, certificado/contraseña inválidos) lanza una excepción de
        // dominio que GlobalExceptionHandler mapea a 400, tras revertir la transacción (rollback total).
        var result = await sender.Send(command, ct);
        return TypedResults.Created($"/api/v1/tenants/{result.TenantId}", result);
    }
}
