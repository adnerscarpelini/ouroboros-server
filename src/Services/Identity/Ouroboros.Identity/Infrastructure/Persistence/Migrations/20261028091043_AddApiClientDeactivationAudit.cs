using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiClientDeactivationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deactivated_at",
                schema: "identity",
                table: "api_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deactivated_by",
                schema: "identity",
                table: "api_clients",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deactivated_at",
                schema: "identity",
                table: "api_clients");

            migrationBuilder.DropColumn(
                name: "deactivated_by",
                schema: "identity",
                table: "api_clients");
        }
    }
}
