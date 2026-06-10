using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// PL-2: añade el soft-delete + anonimización a documents para el borrado con retención fiscal.
    ///   - deleted_at (timestamptz, nullable): marca de borrado lógico. El global query filter del DbContext
    ///     excluye las filas con deleted_at IS NOT NULL de las lecturas normales.
    ///   - is_anonymized (boolean, default false): indica que la PII del comprador fue redactada.
    ///
    /// Ambas columnas se añaden sobre la tabla documents, que YA está bajo FORCE ROW LEVEL SECURITY desde
    /// B3 (las columnas nuevas heredan la política p_documents_tenant — no requieren cambios de RLS).
    /// Idempotente y seguro para despliegues existentes: ADD COLUMN IF NOT EXISTS + nullable/default, sin
    /// reescribir filas existentes.
    /// </summary>
    /// <inheritdoc />
    public partial class B5_DocumentSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE documents ADD COLUMN IF NOT EXISTS deleted_at timestamp with time zone NULL;
                ALTER TABLE documents ADD COLUMN IF NOT EXISTS is_anonymized boolean NOT NULL DEFAULT false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE documents DROP COLUMN IF EXISTS deleted_at;
                ALTER TABLE documents DROP COLUMN IF EXISTS is_anonymized;
            ");
        }
    }
}
