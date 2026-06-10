using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// Provisiona de forma idempotente los dos roles de base de datos del modelo de defensa en profundidad
/// del multi-tenant:
///   - <c>billing_app</c>: rol de aplicación NO propietario y NO superusuario, SIN BYPASSRLS. Es el rol
///     bajo el que corre el DbContext por defecto, por lo que queda restringido por las políticas de RLS
///     (con FORCE ROW LEVEL SECURITY) — no puede ver filas de otros tenants.
///   - <c>billing_privileged</c>: rol con BYPASSRLS para los tres caminos cross-tenant deliberados
///     (auth por hash de api-key, búsqueda por clave de acceso, escaneo all-tenant del retry).
///
/// Se ejecuta al arranque ANTES de <c>MigrateAsync</c>, usando la conexión base (propietario/bootstrap,
/// p. ej. <c>billing_user</c>). Patrón startup-hook, mismo precedente que <see cref="CertificateDataMigrator"/>.
/// Idempotente: si los roles ya existen, sólo re-aplica password y grants (no falla). Esto mantiene la
/// instalación Docker de un solo comando: el entrypoint corre provisioner + migrate sin paso manual de DBA.
/// </summary>
public sealed class RoleProvisioner
{
    private readonly string _bootstrapConnectionString;
    private readonly string _appPassword;
    private readonly string _privilegedPassword;
    private readonly ILogger<RoleProvisioner> _logger;

    public const string AppRoleName = "billing_app";
    public const string PrivilegedRoleName = "billing_privileged";

    public RoleProvisioner(
        IConfiguration configuration,
        ILogger<RoleProvisioner> logger)
    {
        _bootstrapConnectionString = configuration.GetConnectionString("BillingDb")
            ?? "Host=localhost;Database=billing;Username=postgres;Password=postgres";

        // Passwords de los roles desde config/env, con fallback derivado (sólo aceptable en dev/Docker
        // local). En producción DEBEN venir de variables de entorno.
        _appPassword = configuration["Multitenancy:AppRolePassword"]
            ?? Environment.GetEnvironmentVariable("BILLING_APP_DB_PASSWORD")
            ?? "billing_app_change_me";
        _privilegedPassword = configuration["Multitenancy:PrivilegedRolePassword"]
            ?? Environment.GetEnvironmentVariable("BILLING_PRIVILEGED_DB_PASSWORD")
            ?? "billing_privileged_change_me";

        _logger = logger;
    }

    /// <summary>
    /// Crea o actualiza los roles <c>billing_app</c> y <c>billing_privileged</c> y sus grants.
    /// Seguro de re-ejecutar: el bloque DO $$ usa IF NOT EXISTS para CREATE ROLE y siempre re-aplica
    /// ALTER ROLE ... PASSWORD + GRANTs (incluye DEFAULT PRIVILEGES para tablas futuras de migraciones).
    /// </summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_bootstrapConnectionString);
        await connection.OpenAsync(cancellationToken);

        // current_user es el propietario que corre las migraciones; se le concede a billing_app /
        // billing_privileged la pertenencia para heredar el acceso a objetos futuros sin re-grant manual.
        const string sql = @"
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'billing_app') THEN
        CREATE ROLE billing_app LOGIN PASSWORD :appPassword NOBYPASSRLS NOSUPERUSER;
    ELSE
        ALTER ROLE billing_app WITH LOGIN PASSWORD :appPassword NOBYPASSRLS NOSUPERUSER;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'billing_privileged') THEN
        CREATE ROLE billing_privileged LOGIN PASSWORD :privPassword BYPASSRLS NOSUPERUSER;
    ELSE
        ALTER ROLE billing_privileged WITH LOGIN PASSWORD :privPassword BYPASSRLS NOSUPERUSER;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO billing_app, billing_privileged;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO billing_app, billing_privileged;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO billing_app, billing_privileged;

-- Privilegios por defecto para que las tablas/secuencias creadas por futuras migraciones
-- (ejecutadas por el propietario actual) sean accesibles automáticamente.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO billing_app, billing_privileged;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO billing_app, billing_privileged;
";

        // Los marcadores tipo :name dentro de un cuerpo DO $$ son literales de PL/pgSQL y NO son
        // sustituidos por el binding de parámetros de Npgsql. Por eso inyectamos las passwords como
        // literales escapados al estilo quote_literal. Las passwords provienen de configuración del
        // operador (no de entrada de usuario), y aun así se escapan como defensa.
        var resolvedSql = sql
            .Replace(":appPassword", QuoteLiteral(_appPassword))
            .Replace(":privPassword", QuoteLiteral(_privilegedPassword));

        await using var cmd = new NpgsqlCommand(resolvedSql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "RoleProvisioner: roles '{AppRole}' (NOBYPASSRLS) and '{PrivRole}' (BYPASSRLS) provisioned/updated.",
            AppRoleName, PrivilegedRoleName);
    }

    /// <summary>
    /// Escapa un literal de cadena para SQL al estilo de <c>quote_literal</c> de Postgres
    /// (envuelve en comillas simples y duplica las comillas simples internas). Las passwords provienen
    /// de configuración del operador, pero se escapan igualmente como defensa.
    /// </summary>
    private static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''") + "'";
}
