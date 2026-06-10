using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// B8 (secuencial-server-side) — añade el flag por-tenant que selecciona el modo de numeración del
    /// secuencial del comprobante, sin tocar policies de RLS ni datos existentes:
    ///
    ///   Columna <c>auto_generate_secuencial boolean NOT NULL DEFAULT false</c> sobre la tabla
    ///   <c>tenants</c>. <c>false</c> (default) = modo CLIENTE (el secuencial lo provee quien emite, con
    ///   monotonicidad MAX+1); <c>true</c> = modo AUTO (el servidor asigna MAX+1 server-side bajo lock).
    ///   Las filas existentes quedan en <c>false</c> → comportamiento idéntico al previo, sin migración de
    ///   datos (backward-compat R9). Pasar a AUTO es seamless: arranca leyendo el mismo MAX(secuencial).
    ///
    /// Compatibilidad con RLS: la migración solo agrega una columna sobre <c>tenants</c>; NO altera las
    /// policies de RLS de la tabla (creadas en B3). La nueva columna queda automáticamente cubierta por las
    /// policies existentes (que filtran por <c>id</c>/<c>tenant_id</c>).
    ///
    /// El <c>ADD COLUMN</c> de una columna con default constante es instantáneo en Postgres moderno
    /// (metadata-only, sin reescritura de tabla). No se usa <c>CONCURRENTLY</c> porque no se crea ningún
    /// índice ni se toca la tabla <c>documents</c>.
    ///
    /// Idempotencia: <c>IF NOT EXISTS</c> / <c>IF EXISTS</c> permiten re-ejecutar la migración sin error.
    /// </summary>
    /// <inheritdoc />
    public partial class B8_AutoGenerateSecuencial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""tenants""
                    ADD COLUMN IF NOT EXISTS ""auto_generate_secuencial"" boolean NOT NULL DEFAULT false;
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""tenants"" DROP COLUMN IF EXISTS ""auto_generate_secuencial"";
                ");
        }
    }
}
