using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSustentoFieldsToDocumentItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sustento_document_type",
                table: "document_items",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sustento_document_number",
                table: "document_items",
                type: "character varying(39)",
                maxLength: 39,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sustento_document_issue_date",
                table: "document_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sustento_document_auth_number",
                table: "document_items",
                type: "character varying(49)",
                maxLength: 49,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sustento_document_type",
                table: "document_items");

            migrationBuilder.DropColumn(
                name: "sustento_document_number",
                table: "document_items");

            migrationBuilder.DropColumn(
                name: "sustento_document_issue_date",
                table: "document_items");

            migrationBuilder.DropColumn(
                name: "sustento_document_auth_number",
                table: "document_items");
        }
    }
}
