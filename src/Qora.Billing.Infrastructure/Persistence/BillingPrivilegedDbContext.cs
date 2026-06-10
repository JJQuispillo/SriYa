using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// DbContext que se conecta como el rol <c>billing_privileged</c> (BYPASSRLS). Comparte el mismo modelo
/// que <see cref="BillingDbContext"/> pero usa una cadena de conexión distinta y NO lleva el interceptor
/// de tenant.
///
/// Se inyecta ÚNICAMENTE en los tres caminos cross-tenant deliberados, donde el acceso a múltiples
/// tenants es intencional (no un estado accidental de la GUC):
///   (a) autenticación por hash de API key (antes de conocer el tenant),
///   (b) búsqueda de documento por clave de acceso,
///   (c) escaneo all-tenant del servicio de reintentos del SRI.
///
/// Mantiene <c>IgnoreQueryFilters</c> en esas consultas y, además, corre sobre la conexión privilegiada
/// para que sigan funcionando cuando P1 active los filtros fail-closed.
/// </summary>
public sealed class BillingPrivilegedDbContext : BillingDbContext
{
    public BillingPrivilegedDbContext(
        DbContextOptions<BillingPrivilegedDbContext> options,
        IConfiguration configuration)
        : base(options, configuration)
    {
    }
}
