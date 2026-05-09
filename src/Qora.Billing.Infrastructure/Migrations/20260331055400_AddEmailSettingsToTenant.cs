using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettingsToTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "email_enabled",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "email_provider",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "sender_email",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sender_name",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_host",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_password",
                table: "tenants",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "smtp_port",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_user",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "use_ssl",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_enabled",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "email_provider",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "sender_email",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "sender_name",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "smtp_host",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "smtp_password",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "smtp_port",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "smtp_user",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "use_ssl",
                table: "tenants");
        }
    }
}
