using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuiaRemisionDestinatarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_destinatarios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identificacion_destinatario = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    razon_social_destinatario = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    dir_destinatario = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    motivo_traslado = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ruta_entrega = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    doc_aduanero_unico = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    cod_estab_destino = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    ruc_transportista = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    rise = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_destinatarios", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_destinatarios_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_destinatario_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destinatario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    codigo_interno = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    descripcion_detalle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    cantidad_detalle = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_destinatario_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_destinatario_items_document_destinatarios_destinatario_id",
                        column: x => x.destinatario_id,
                        principalTable: "document_destinatarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_destinatarios_document_id",
                table: "document_destinatarios",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_destinatario_items_destinatario_id",
                table: "document_destinatario_items",
                column: "destinatario_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_destinatario_items");

            migrationBuilder.DropTable(
                name: "document_destinatarios");
        }
    }
}
