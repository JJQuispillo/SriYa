using Microsoft.EntityFrameworkCore;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Middleware que extrae el TenantId de los claims del usuario autenticado
/// y lo establece en ITenantContext (capa de aplicación), en BillingDbContext (filtros de consulta)
/// y en ITenantSession (que alimenta el interceptor que fija la GUC de RLS).
///
/// Para las solicitudes con tenant, ABRE una transacción ambiental sobre el BillingDbContext y la
/// confirma al final. Esto es necesario porque la GUC <c>app.current_tenant</c> se fija con
/// <c>set_config(..., true)</c> (transaction-local) por el interceptor: sin una transacción abierta,
/// el ajuste no tendría efecto y RLS no vería el tenant. La naturaleza transaction-local también
/// garantiza que la GUC se descarte al hacer commit/rollback, evitando fugas a la siguiente
/// reutilización de la conexión desde el pool (seguro con PgBouncer en modo transaction).
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
        ITenantSession tenantSession,
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

        if (!tenantId.HasValue)
        {
            // Camino sin tenant (p. ej. autenticación, bootstrap): no se abre transacción ambiental.
            await _next(context);
            return;
        }

        tenantContext.SetTenantId(tenantId.Value);
        dbContext.SetTenantId(tenantId.Value);
        tenantSession.SetTenant(tenantId.Value);

        // Abrir una transacción ambiental para que la GUC pueda fijarse transaction-local.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(context.RequestAborted);
        tenantSession.SetInTransaction(true);

        // Fijar la GUC app.current_tenant UNA sola vez, como su propio comando, dentro de la transacción.
        // set_config(..., true) es transaction-local: se descarta al commit/rollback y no se filtra a la
        // siguiente reutilización de la conexión desde el pool. Toda consulta posterior en esta transacción
        // (incluidas las que usan IgnoreQueryFilters sobre este contexto) verá la GUC y RLS la aplicará.
        // Nota: NO se antepone a cada comando (eso produce un batch multi-sentencia cuyo PRIMER result set
        // —el texto de set_config— corrompe la materialización de las lecturas en Postgres real).
        // Sólo aplica al proveedor Npgsql; el proveedor InMemory de las pruebas no soporta SQL crudo ni
        // necesita RLS (el aislamiento allí lo dan los global query filters fail-closed).
        if (dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT set_config('app.current_tenant', {0}, true)",
                [tenantId.Value.ToString()],
                context.RequestAborted);
        }

        try
        {
            await _next(context);
            await transaction.CommitAsync(context.RequestAborted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            tenantSession.SetInTransaction(false);
        }
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
