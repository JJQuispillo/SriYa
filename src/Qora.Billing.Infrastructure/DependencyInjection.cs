using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.BackgroundServices;
using Qora.Billing.Infrastructure.Caching;
using Qora.Billing.Infrastructure.Email;
using Qora.Billing.Infrastructure.Pdf;
using Qora.Billing.Infrastructure.Persistence;
using Qora.Billing.Infrastructure.Persistence.Repositories;
using Qora.Billing.Infrastructure.Signing;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure;

/// <summary>
/// Métodos de extensión para registrar todos los servicios de la capa de Infrastructure.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── Sesión de tenant (alcance por solicitud) ─────────────────────────────────
        // TenantSession lo rellena TenantContextMiddleware tras la autenticación. El middleware abre la
        // transacción ambiental y fija la GUC app.current_tenant (set_config(..., true)) UNA sola vez
        // como su propio comando — no se antepone a cada comando (eso produce un batch multi-sentencia
        // cuyo primer result set corrompe la materialización de las lecturas en Postgres real).
        services.AddScoped<TenantSession>();
        services.AddScoped<ITenantSession>(sp => sp.GetRequiredService<TenantSession>());

        var baseConnectionString = configuration.GetConnectionString("BillingDb")
            ?? "Host=localhost;Database=billing;Username=postgres;Password=postgres";

        // El DbContext de la app corre como billing_app (NO BYPASSRLS) → restringido por RLS.
        // El DbContext privilegiado corre como billing_privileged (BYPASSRLS) para los 3 caminos
        // cross-tenant. Ambas cadenas se derivan de la base intercambiando usuario/contraseña, para
        // no exigir nuevas connection strings al operador (instalación Docker de un solo comando).
        var appConnectionString = BuildRoleConnectionString(
            baseConnectionString,
            RoleProvisioner.AppRoleName,
            configuration["Multitenancy:AppRolePassword"]
                ?? Environment.GetEnvironmentVariable("BILLING_APP_DB_PASSWORD")
                ?? "billing_app_change_me");

        var privilegedConnectionString = BuildRoleConnectionString(
            baseConnectionString,
            RoleProvisioner.PrivilegedRoleName,
            configuration["Multitenancy:PrivilegedRolePassword"]
                ?? Environment.GetEnvironmentVariable("BILLING_PRIVILEGED_DB_PASSWORD")
                ?? "billing_privileged_change_me");

        // Base de datos (contexto por defecto, rol billing_app — restringido por RLS).
        services.AddDbContext<BillingDbContext>(options =>
        {
            options.UseNpgsql(
                appConnectionString,
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name));

            // TenantConfiguration y ElectronicSignatureConfiguration requieren la clave de
            // cifrado por constructor, por lo que se aplican manualmente en OnModelCreating y
            // se excluyen del scan del assembly. EF igual las detecta durante el scan y emite
            // este warning; lo silenciamos porque es esperado y benigno.
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning));
        });

        // Contexto privilegiado (rol billing_privileged, BYPASSRLS, SIN interceptor de tenant).
        // Inyectado sólo en los repos de los 3 caminos cross-tenant.
        services.AddDbContext<BillingPrivilegedDbContext>(options =>
        {
            options.UseNpgsql(
                privilegedConnectionString,
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name));
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning));
        });

        // Repositorios
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IElectronicSignatureRepository, ElectronicSignatureRepository>();
        services.AddScoped<IDocumentEventRepository, DocumentEventRepository>();
        services.AddScoped<ISriTaxCodeRepository, SriTaxCodeRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Idempotencia (II-1): store sobre el contexto normal (billing_app, acotado por RLS).
        services.Configure<IdempotencySettings>(configuration.GetSection(IdempotencySettings.SectionName));
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // Ciclo de vida por emisor (PL-1 exportación / PL-2 borrado con retención). Corre sobre el contexto
        // normal (billing_app, acotado por RLS) → tenant-scoped. AllowHardDeleteAuthorized por defecto false.
        services.Configure<LifecycleSettings>(configuration.GetSection(LifecycleSettings.SectionName));
        services.AddScoped<ITenantLifecycleService, TenantLifecycleService>();

        // Onboarding atómico (OB-1). Corre sobre el contexto PRIVILEGIADO (billing_privileged, BYPASSRLS):
        // al arrancar el bootstrap el tenant aún no existe ni hay GUC de tenant, así que bajo billing_app
        // (FORCE RLS, fail-closed) los INSERT serían rechazados. Compone tenant + certificado + API key en
        // una sola transacción con rollback total.
        services.AddScoped<ITenantBootstrapService, TenantBootstrapService>();

        // Constructores de XML (registros singleton concretos, uno por tipo de documento)
        services.AddSingleton<FacturaXmlBuilder>();
        services.AddSingleton<NotaCreditoXmlBuilder>();
        services.AddSingleton<NotaDebitoXmlBuilder>();
        services.AddSingleton<LiquidacionCompraXmlBuilder>();
        services.AddSingleton<GuiaRemisionXmlBuilder>();
        services.AddSingleton<ComprobanteRetencionXmlBuilder>();

        // Mapeo de la interfaz IXmlGenerator (usada por FacturaStrategy)
        services.AddSingleton<IXmlGenerator, FacturaXmlBuilder>();

        // Firma de documentos
        services.AddSingleton<IDocumentSigner, XadesBesSigner>();

        // Cliente SOAP del SRI con resiliencia Polly (retry, circuit breaker, timeout)
        services.Configure<SriConfiguration>(configuration.GetSection(SriConfiguration.SectionName));
        services.Configure<SriRetryConfiguration>(configuration.GetSection(SriRetryConfiguration.SectionName));
        services.AddSingleton<IValidateOptions<SriConfiguration>, SriConfigurationValidator>();
        services.AddSriClientWithResilience(configuration);

        // Seam de cache distribuido (redis-ready-cache-limiter). Registra un único IDistributedCache:
        // in-memory por defecto, Redis al activar Cache:Provider=Redis. NINGÚN consumidor cableado (D3)
        // y el rate limiter permanece intacto (D1). Inerte hacia Redis con el default InMemory.
        services.AddCachingServices(configuration);

        // Emisión atómica (sri-emision-atomicidad). Flujo de pre-reserva + 5 checkpoints siempre activo.
        services.Configure<EmissionOptions>(configuration.GetSection(EmissionOptions.SectionName));

        // Reconciliador (design D4/D8/D9.a). Incondicional — red de seguridad del bug N1.
        services.Configure<SriReconciliationOptions>(configuration.GetSection(SriReconciliationOptions.SectionName));

        // Reintento de RIDE PDF (ride-pdf-retry). Red de seguridad del best-effort que se traga la
        // excepción al generar el RIDE en la emisión: regenera el RIDE de los documentos autorizados
        // con ride_generated_at NULL.
        services.Configure<RidePdfRetryOptions>(configuration.GetSection(RidePdfRetryOptions.SectionName));

        // Servicios en segundo plano
        services.AddHostedService<SriRetryService>();
        services.AddHostedService<SriReconciliationService>();
        services.AddHostedService<RidePdfRetryService>();

        // Estrategias por tipo de documento (patrón IEnumerable<IDocumentTypeStrategy>)
        services.AddScoped<IDocumentTypeStrategy, FacturaStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaCreditoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaDebitoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, LiquidacionCompraStrategy>();
        services.AddScoped<IDocumentTypeStrategy, GuiaRemisionStrategy>();
        services.AddScoped<IDocumentTypeStrategy, ComprobanteRetencionStrategy>();

        // Generación de PDF / RIDE
        services.AddPdfServices();

        // Envío de email
        services.Configure<QoraEmailSettings>(configuration.GetSection(QoraEmailSettings.SectionName));
        services.AddScoped<QoraEmailProvider>();
        services.AddScoped<CustomEmailProvider>();
        services.AddScoped<IEmailService, SmtpEmailService>();

        return services;
    }

    /// <summary>
    /// Registra el seam de cache distribuido (<see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>):
    /// enlaza y valida <see cref="CacheOptions"/>, y selecciona el provider según <see cref="CacheOptions.Provider"/>.
    /// InMemory (default) ⇒ <c>AddDistributedMemoryCache()</c>; Redis ⇒ <c>AddStackExchangeRedisCache(...)</c>.
    /// Idempotente: una sola registración efectiva de <c>IDistributedCache</c>. Inerte hacia Redis con el default.
    /// </summary>
    public static IServiceCollection AddCachingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(CacheOptions.SectionName);

        services.Configure<CacheOptions>(section);
        services.AddSingleton<IValidateOptions<CacheOptions>, CacheOptionsValidator>();

        // Lee el provider del binding directo para decidir QUÉ implementación de IDistributedCache registrar.
        // El IValidateOptions corre al construir el provider (fail-fast) sobre el mismo binding.
        var cacheOptions = section.Get<CacheOptions>() ?? new CacheOptions();

        if (cacheOptions.Provider == CacheProvider.Redis)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = cacheOptions.RedisConnectionString;
                options.InstanceName = cacheOptions.InstanceName;
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        return services;
    }

    /// <summary>
    /// Deriva una cadena de conexión para un rol concreto a partir de la cadena base, intercambiando
    /// el usuario y la contraseña. Mantiene host/puerto/base de datos/SSL/etc. de la base, para que
    /// el operador sólo deba configurar una connection string (instalación Docker de un solo comando).
    /// </summary>
    private static string BuildRoleConnectionString(string baseConnectionString, string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Username = role,
            Password = password,
        };
        return builder.ConnectionString;
    }
}
