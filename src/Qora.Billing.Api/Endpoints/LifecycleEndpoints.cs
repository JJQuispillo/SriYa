using MediatR;
using Qora.Billing.Api.Middleware;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.Interfaces;
// ITenantContext vive en Application.Interfaces; ServiceTokenAuthenticationHandler en Api.Middleware.

namespace Qora.Billing.Api.Endpoints;

/// <summary>
/// Endpoints de ciclo de vida por emisor (PL-1 exportación / PL-2 borrado con retención).
///
/// Requieren autenticación ServiceToken y un encabezado X-Tenant-Id que identifica al emisor objetivo:
/// son operaciones ACOTADAS a ese tenant (no all-tenant). TenantContextMiddleware fija el tenant del
/// X-Tenant-Id en el contexto (RLS + filtros), de modo que el servicio sólo ve/afecta los datos de ese
/// emisor. El borrado exporta SIEMPRE antes de tocar cualquier fila (export-always).
/// </summary>
public static class LifecycleEndpoints
{
    public static RouteGroupBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/lifecycle")
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = ServiceTokenAuthenticationHandler.SchemeName
            })
            .WithTags("Lifecycle")
            .WithOpenApi();

        group.MapGet("/export", ExportTenant)
            .WithName("ExportTenant")
            .WithSummary("Export all data of a single emisor (tenant) as a ZIP (service-to-service only)");

        group.MapDelete("/", DeleteTenantData)
            .WithName("DeleteTenantData")
            .WithSummary("Scoped delete of a single emisor's data with retention policy (service-to-service only)");

        return group;
    }

    private static IResult ExportTenant(ITenantContext tenantContext, ISender sender)
    {
        var tenantId = GetRequiredTenantId(tenantContext);
        var fileName = $"emisor-{tenantId}-export.zip";

        // Stream del ZIP directamente al cuerpo de la respuesta (sin bufferizar todo en memoria).
        return Results.Stream(
            async stream => await sender.Send(new ExportTenantCommand(tenantId, stream)),
            contentType: "application/zip",
            fileDownloadName: fileName);
    }

    private static IResult DeleteTenantData(ITenantContext tenantContext, ISender sender)
    {
        var tenantId = GetRequiredTenantId(tenantContext);

        // PL-2: el borrado exporta SIEMPRE primero (export-always). El ZIP de respaldo se devuelve como
        // descarga en el cuerpo de la respuesta; el resumen de lo borrado/anonimizado se anota en
        // encabegados antes de iniciar el stream y se registra en el log del servicio.
        var fileName = $"emisor-{tenantId}-pre-delete-export.zip";

        return Results.Stream(
            async stream =>
            {
                // El servicio exporta a este stream (el cuerpo de la respuesta) y luego borra. El resumen
                // se devuelve, pero como ya empezó el stream del cuerpo no podemos añadir más encabezados:
                // el contrato observable de export-always es que el ZIP de respaldo se entrega completo.
                _ = await sender.Send(new DeleteTenantDataCommand(tenantId, stream));
            },
            contentType: "application/zip",
            fileDownloadName: fileName);
    }

    private static Guid GetRequiredTenantId(ITenantContext tenantContext)
    {
        return tenantContext.TenantId
            ?? throw new UnauthorizedAccessException(
                "Las operaciones de ciclo de vida requieren un emisor objetivo (encabezado X-Tenant-Id).");
    }
}
