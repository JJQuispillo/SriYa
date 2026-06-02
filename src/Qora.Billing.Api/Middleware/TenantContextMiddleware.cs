using Qora.Billing.Application.Interfaces;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Middleware que extrae el TenantId de los claims del usuario autenticado
/// y lo establece tanto en ITenantContext (para la capa de aplicación) como en
/// BillingDbContext (para los filtros de consulta multi-tenant).
/// </summary>
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;

    public TenantContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        BillingDbContext dbContext)
    {
        Guid? tenantId = null;

        // 1. Intentar obtener el TenantId desde los claims (establecido por el manejador de autenticación ApiKey)
        var tenantIdClaim = context.User.FindFirst("TenantId")?.Value;
        if (!string.IsNullOrEmpty(tenantIdClaim) && Guid.TryParse(tenantIdClaim, out var claimTenantId))
        {
            tenantId = claimTenantId;
        }

        // 2. Para la autenticación con ServiceToken, permitir que el encabezado X-Tenant-Id especifique el tenant
        //    Esto permite que las llamadas entre servicios operen en nombre de un tenant específico.
        if (!tenantId.HasValue
            && context.User.Identity?.AuthenticationType == ServiceTokenAuthenticationHandler.SchemeName
            && context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantIdHeader)
            && Guid.TryParse(tenantIdHeader.FirstOrDefault(), out var headerTenantId))
        {
            tenantId = headerTenantId;
        }

        if (tenantId.HasValue)
        {
            tenantContext.SetTenantId(tenantId.Value);
            dbContext.SetTenantId(tenantId.Value);
        }

        await _next(context);
    }
}

/// <summary>
/// Método de extensión para registrar el TenantContextMiddleware en el pipeline.
/// </summary>
public static class TenantContextMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
