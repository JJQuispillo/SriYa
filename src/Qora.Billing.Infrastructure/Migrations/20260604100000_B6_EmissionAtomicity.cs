using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// B6 (sri-emision-atomicidad) — añade dos índices parciales sobre la tabla
    /// <c>documents</c> para soportar el nuevo flujo de pre-reservación atómica
    /// (REQs REQ-EMI-026, REQ-EMI-027, REQ-EMI-028) sin modificar columnas,
    /// constraints ni datos:
    ///
    ///   1. <c>ix_documents_tenant_secuencial</c> — compuesto sobre
    ///      (tenant_id, document_type, estab, pto_emision, secuencial DESC) con
    ///      filtro parcial <c>WHERE estab IS NOT NULL AND pto_emision IS NOT NULL
    ///      AND secuencial IS NOT NULL</c>. Respalda el lookup
    ///      <c>SELECT MAX(secuencial) ... FOR UPDATE</c> ejecutado en
    ///      <c>IDocumentRepository.GetMaxSecuencialWithLockAsync</c> dentro de la
    ///      transacción de pre-reservación (REQ-EMI-009).
    ///
    ///   2. <c>ix_documents_senttosri_createdat</c> — parcial sobre (created_at)
    ///      con filtro <c>WHERE status = 'SentToSri'</c>. Respalda el barrido del
    ///      reconciliador <c>SriReconciliationService</c> que busca documentos
    ///      varados en <c>SentToSri</c> para re-chequear su autorización en el
    ///      SRI (REQ-EMI-022). Solo indexa el conjunto de filas candidatas a
    ///      rescate, así el índice es pequeño y el barrido barato.
    ///
    /// IMPORTANTE — Concurrencia sin downtime:
    /// Ambas sentencias usan <c>CREATE INDEX CONCURRENTLY</c> para que Postgres
    /// NO tome un <c>ACCESS EXCLUSIVE</c> lock sobre la tabla <c>documents</c>
    /// durante la construcción del índice. <c>CONCURRENTLY</c> es incompatible
    /// con la transacción de migración por defecto de EF Core, por lo que cada
    /// llamada pasa <c>suppressTransaction: true</c> como parámetro nombrado a
    /// <see cref="MigrationBuilder.Sql(string, bool)"/>. La migración completa
    /// se aplica de forma operacional (antes de desplegar el código de runtime
    /// de PR #2), típicamente 1h antes del canary. Ver runbook
    /// <c>docs/runbooks/sri-emision-atomicity.md</c>.
    ///
    /// Idempotencia: <c>IF NOT EXISTS</c> permite re-ejecutar la migración sin
    /// error si un operador la disparó dos veces.
    ///
    /// El <c>Down</c> usa <c>DROP INDEX IF EXISTS</c> (sin CONCURRENTLY) porque
    /// el downgrade NUNCA se ejecuta dentro de una transacción de aplicación
    /// en producción: o se hace desde el script operacional (que ya está
    /// fuera de la transacción de migración) o se documenta el procedimiento
    /// en el runbook. Por consistencia con el resto de migraciones, dejamos
    /// el patrón <c>DROP ... IF EXISTS</c> plano.
    /// </summary>
    /// <inheritdoc />
    public partial class B6_EmissionAtomicity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Índice compuesto para MAX(secuencial) en el lock pesimista del
            //    pre-reservation. Orden de columnas idéntico al WHERE del lookup
            //    (tenant_id, document_type, estab, pto_emision) + secuencial DESC
            //    (Postgres puede usar el índice para un MAX sin sort).
            //    Filtro parcial alineado con la condición del constraint único
            //    parcial `ux_documents_business_identity` (creado en B4).
            migrationBuilder.Sql(
                @"
                    CREATE INDEX CONCURRENTLY IF NOT EXISTS ""ix_documents_tenant_secuencial""
                    ON ""documents"" (""tenant_id"", ""document_type"", ""estab"", ""pto_emision"", ""secuencial"" DESC)
                    WHERE ""estab"" IS NOT NULL AND ""pto_emision"" IS NOT NULL AND ""secuencial"" IS NOT NULL;
                ",
                suppressTransaction: true);

            // 2) Índice parcial para el sweep del reconciliador.
            //    `status` se almacena como VARCHAR(20) (string del enum) por
            //    `HasConversion<string>()` en DocumentConfiguration — el valor
            //    guardado es la representación textual del enum
            //    (`DocumentStatus.SentToSri` → 'SentToSri'), NO el ordinal.
            migrationBuilder.Sql(
                @"
                    CREATE INDEX CONCURRENTLY IF NOT EXISTS ""ix_documents_senttosri_createdat""
                    ON ""documents"" (""created_at"")
                    WHERE ""status"" = 'SentToSri';
                ",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El Down se ejecuta operacionalmente fuera de la transacción de
            // aplicación (ver runbook), por lo que CONCURRENTLY NO es necesario
            // aquí. El orden es inverso al Up: primero el más nuevo.
            migrationBuilder.Sql(
                @"
                    DROP INDEX IF EXISTS ""ix_documents_senttosri_createdat"";
                ",
                suppressTransaction: true);

            migrationBuilder.Sql(
                @"
                    DROP INDEX IF EXISTS ""ix_documents_tenant_secuencial"";
                ",
                suppressTransaction: true);
        }
    }
}
