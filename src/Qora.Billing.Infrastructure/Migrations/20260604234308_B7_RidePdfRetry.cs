using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// B7 (ride-pdf-retry) — añade el soporte de persistencia para la red de seguridad de generación
    /// del RIDE PDF (<c>RidePdfRetryService</c>), sin tocar policies de RLS ni datos existentes:
    ///
    ///   1. Columna <c>ride_generated_at timestamptz NULL</c> — marca temporal de éxito de la
    ///      generación del RIDE. El RIDE NO se persiste (se genera on-demand); esta columna es el único
    ///      marcador de que la generación en la emisión tuvo éxito. Las filas existentes quedan en NULL
    ///      y el barrido las regenera.
    ///
    ///   2. Columna <c>ride_retry_count integer NOT NULL DEFAULT 0</c> — acota los reintentos
    ///      best-effort del barrido (deja de reintentar al alcanzar <c>MaxRetries</c>).
    ///
    ///   3. Índice parcial <c>ix_documents_authorized_ride_pending</c> sobre (<c>processed_at</c>) con
    ///      filtro <c>WHERE status = 'Authorized' AND ride_generated_at IS NULL</c>. Respalda el barrido
    ///      <c>SELECT ... FOR UPDATE SKIP LOCKED</c> de
    ///      <c>IDocumentRepository.GetAuthorizedMissingRidePdfAsync</c>. Solo indexa el conjunto de filas
    ///      candidatas a regeneración → índice pequeño y barrido barato (mismo patrón que el índice del
    ///      reconciliador en B6).
    ///
    /// Compatibilidad con RLS: la migración solo agrega columnas e índice; NO altera las policies de RLS
    /// ni el FORCE ROW LEVEL SECURITY de la tabla <c>documents</c> (creados en B3). Las nuevas columnas
    /// quedan automáticamente cubiertas por las policies existentes (que filtran por <c>tenant_id</c>).
    /// El barrido cross-tenant del servicio corre bajo el rol privilegiado (BYPASSRLS), igual que el
    /// reconciliador.
    ///
    /// IMPORTANTE — Concurrencia sin downtime:
    /// La creación del índice usa <c>CREATE INDEX CONCURRENTLY</c> para que Postgres NO tome un
    /// <c>ACCESS EXCLUSIVE</c> lock sobre <c>documents</c>. <c>CONCURRENTLY</c> es incompatible con la
    /// transacción de migración por defecto de EF Core, por lo que la sentencia pasa
    /// <c>suppressTransaction: true</c>. El <c>ADD COLUMN</c> de columnas nullable / con default es
    /// instantáneo en Postgres moderno (metadata-only) y no requiere reescritura de tabla.
    ///
    /// Idempotencia: <c>IF NOT EXISTS</c> permite re-ejecutar la migración sin error.
    /// </summary>
    /// <inheritdoc />
    public partial class B7_RidePdfRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Marca de éxito de la generación del RIDE (nullable → filas existentes en NULL).
            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""documents""
                    ADD COLUMN IF NOT EXISTS ""ride_generated_at"" timestamp with time zone NULL;
                ");

            // 2) Contador de reintentos del barrido (NOT NULL DEFAULT 0 → backfill instantáneo).
            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""documents""
                    ADD COLUMN IF NOT EXISTS ""ride_retry_count"" integer NOT NULL DEFAULT 0;
                ");

            // 3) Índice parcial para el barrido del RidePdfRetryService. Solo cubre filas Authorized cuyo
            //    RIDE aún no se generó. `status` se almacena como VARCHAR (HasConversion<string>()) → el
            //    valor guardado es 'Authorized'. CONCURRENTLY para no bloquear la tabla.
            migrationBuilder.Sql(
                @"
                    CREATE INDEX CONCURRENTLY IF NOT EXISTS ""ix_documents_authorized_ride_pending""
                    ON ""documents"" (""processed_at"")
                    WHERE ""status"" = 'Authorized' AND ""ride_generated_at"" IS NULL;
                ",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El Down se ejecuta operacionalmente fuera de la transacción de aplicación (ver runbook),
            // por lo que CONCURRENTLY no es necesario aquí. Orden inverso al Up.
            migrationBuilder.Sql(
                @"
                    DROP INDEX IF EXISTS ""ix_documents_authorized_ride_pending"";
                ",
                suppressTransaction: true);

            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""documents"" DROP COLUMN IF EXISTS ""ride_retry_count"";
                ");

            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""documents"" DROP COLUMN IF EXISTS ""ride_generated_at"";
                ");
        }
    }
}
