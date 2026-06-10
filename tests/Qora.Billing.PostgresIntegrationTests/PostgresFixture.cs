using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Settings;
using Qora.Billing.Infrastructure.Pdf;
using Qora.Billing.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// Levanta un Postgres real (postgres:16-alpine) vía Testcontainers y lo cablea EXACTAMENTE como
/// producción: corre <see cref="RoleProvisioner"/> (crea billing_app NO-BYPASSRLS + billing_privileged
/// BYPASSRLS), luego aplica las migraciones como el rol propietario (que activan FORCE RLS + políticas).
///
/// La app real se conecta como billing_app (restringido por RLS); los 3 caminos cross-tenant usan
/// billing_privileged. El fixture expone fábricas para crear contextos bajo cada rol, replicando el
/// sobre transaccional + interceptor de GUC que el middleware abre en producción, de modo que las
/// pruebas ejercitan la misma mecánica de aislamiento (imposible con el proveedor InMemory).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string OwnerUser = "billing_owner";
    private const string OwnerPassword = "owner_pwd_test";
    private const string AppPassword = "app_pwd_test";
    private const string PrivilegedPassword = "priv_pwd_test";
    private const string EncryptionKey = "test-encryption-key-32-chars-long!";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing")
        .WithUsername(OwnerUser)
        .WithPassword(OwnerPassword)
        .Build();

    private string _ownerConnectionString = string.Empty;

    /// <summary>Cadena de conexión del rol billing_app (NO BYPASSRLS, restringido por RLS).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Cadena de conexión del rol billing_privileged (BYPASSRLS).</summary>
    public string PrivilegedConnectionString { get; private set; } = string.Empty;

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = EncryptionKey,
            })
            .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _ownerConnectionString = _container.GetConnectionString();

        var ownerBuilder = new NpgsqlConnectionStringBuilder(_ownerConnectionString);

        AppConnectionString = new NpgsqlConnectionStringBuilder(_ownerConnectionString)
        {
            Username = RoleProvisioner.AppRoleName,
            Password = AppPassword,
        }.ConnectionString;

        PrivilegedConnectionString = new NpgsqlConnectionStringBuilder(_ownerConnectionString)
        {
            Username = RoleProvisioner.PrivilegedRoleName,
            Password = PrivilegedPassword,
        }.ConnectionString;

        // 1. Provisionar roles como el propietario (mismo orden que Program.cs: antes de migrar).
        var provisionerConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BillingDb"] = _ownerConnectionString,
                ["Multitenancy:AppRolePassword"] = AppPassword,
                ["Multitenancy:PrivilegedRolePassword"] = PrivilegedPassword,
            })
            .Build();

        var provisioner = new RoleProvisioner(provisionerConfig, NullLogger<RoleProvisioner>.Instance);
        await provisioner.ProvisionAsync();

        // 2. Migrar como el propietario (DDL + FORCE RLS + políticas), igual que Program.cs.
        await using var ownerContext = CreateOwnerContext();
        await ownerContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Contexto propietario (puede ejecutar DDL y, por ser el dueño con FORCE RLS, NO evade RLS).
    /// Se usa sólo para migrar y para sembrar datos vía SQL crudo en una conexión separada.
    /// </summary>
    public BillingDbContext CreateOwnerContext()
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(_ownerConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning))
            .Options;
        return new BillingDbContext(options, BuildConfiguration());
    }

    /// <summary>
    /// Contexto de la app (rol billing_app, restringido por RLS). Si <paramref name="tenantId"/> es nulo,
    /// NO se establece tenant (escenario fail-closed). Si se indica un tenant, fija el filtro de EF; el
    /// llamador debe abrir la transacción ambiental y fijar la GUC vía <see cref="SetTenantGucAsync"/>,
    /// replicando lo que hace TenantContextMiddleware en producción.
    /// </summary>
    public (BillingDbContext Context, TenantSession Session) CreateAppContext(Guid? tenantId)
    {
        var session = new TenantSession();

        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(AppConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning))
            .Options;

        var context = new BillingDbContext(options, BuildConfiguration());

        if (tenantId.HasValue)
        {
            context.SetTenantId(tenantId.Value);
            session.SetTenant(tenantId.Value);
        }

        return (context, session);
    }

    /// <summary>
    /// Fija la GUC transaction-local app.current_tenant en el contexto dado, como su propio comando
    /// (igual que TenantContextMiddleware tras BeginTransaction). Debe llamarse dentro de una transacción.
    /// </summary>
    public static Task SetTenantGucAsync(BillingDbContext context, Guid tenantId) =>
        context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant', {0}, true)", tenantId.ToString());

    /// <summary>
    /// Contexto privilegiado (rol billing_privileged, BYPASSRLS, SIN interceptor) — el que usan los
    /// 3 caminos cross-tenant deliberados.
    /// </summary>
    public BillingPrivilegedDbContext CreatePrivilegedContext()
    {
        var options = new DbContextOptionsBuilder<BillingPrivilegedDbContext>()
            .UseNpgsql(PrivilegedConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning))
            .Options;
        return new BillingPrivilegedDbContext(options, BuildConfiguration());
    }

    /// <summary>
    /// Abre una conexión cruda como el rol billing_app — para probar que RLS bloquea incluso SQL
    /// crudo que NO pasa por los filtros de EF.
    /// </summary>
    public NpgsqlConnection CreateRawAppConnection() => new(AppConnectionString);

    /// <summary>
    /// Construye un <see cref="TenantBootstrapService"/> sobre el contexto PRIVILEGIADO (billing_privileged,
    /// BYPASSRLS), igual que en producción: el bootstrap aprovisiona un emisor que aún no existe, por lo que
    /// no hay GUC de tenant y debe correr en la conexión privilegiada. <paramref name="environment"/> controla
    /// el prefijo de la API key ("Production" → qora_live_, otro → qora_test_).
    /// </summary>
    public TenantBootstrapService CreateBootstrapService(string environment = "Test")
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new ApiKeySettings { Environment = environment });
        return new TenantBootstrapService(
            CreatePrivilegedContext(),
            settings,
            NullLogger<TenantBootstrapService>.Instance);
    }

    /// <summary>
    /// Construye un <see cref="TenantLifecycleService"/> sobre el contexto dado (acotado por tenant,
    /// billing_app + RLS), con el valor indicado de AllowHardDeleteAuthorized. Asegura la licencia
    /// Community de QuestPDF para que la generación de RIDE no falle en las pruebas.
    /// </summary>
    public static TenantLifecycleService CreateLifecycleService(
        BillingDbContext context, bool allowHardDeleteAuthorized)
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var settings = Microsoft.Extensions.Options.Options.Create(new LifecycleSettings
        {
            AllowHardDeleteAuthorized = allowHardDeleteAuthorized,
            ExportFormat = "zip",
            ExportBeforeDelete = true,
        });

        return new TenantLifecycleService(
            context,
            new RideGenerator(),
            settings,
            NullLogger<TenantLifecycleService>.Instance);
    }
}

/// <summary>
/// Colección xUnit para compartir un único contenedor Postgres entre todas las clases de prueba
/// (arrancar el contenedor es costoso; se hace una sola vez).
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
