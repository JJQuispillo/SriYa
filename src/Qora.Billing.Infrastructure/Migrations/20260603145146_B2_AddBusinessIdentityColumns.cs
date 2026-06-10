using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class B2_AddBusinessIdentityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "estab",
                table: "documents",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pto_emision",
                table: "documents",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secuencial",
                table: "documents",
                type: "char(9)",
                nullable: true);

            // Backfill de la identidad de negocio desde el jsonb issuer_info existente, para que
            // las filas ya persistidas queden pobladas. Idempotente: sólo escribe donde la columna
            // está NULL y la clave existe en el jsonb. Seguro de re-ejecutar en despliegues existentes.
            migrationBuilder.Sql(@"
                UPDATE documents
                SET estab = LEFT(issuer_info->>'estab', 3)
                WHERE estab IS NULL AND issuer_info ? 'estab';

                UPDATE documents
                SET pto_emision = LEFT(issuer_info->>'ptoEmi', 3)
                WHERE pto_emision IS NULL AND issuer_info ? 'ptoEmi';

                UPDATE documents
                SET secuencial = LEFT(issuer_info->>'secuencial', 9)
                WHERE secuencial IS NULL AND issuer_info ? 'secuencial';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "estab",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "pto_emision",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "secuencial",
                table: "documents");
        }
    }
}
