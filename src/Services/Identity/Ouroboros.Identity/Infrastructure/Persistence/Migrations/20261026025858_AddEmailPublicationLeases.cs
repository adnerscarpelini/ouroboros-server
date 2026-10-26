using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailPublicationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                schema: "identity",
                table: "email_intents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                schema: "identity",
                table: "email_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until",
                schema: "identity",
                table: "email_intents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "identity",
                table: "email_intents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_failure_code",
                schema: "identity",
                table: "email_intents");

            migrationBuilder.DropColumn(
                name: "lease_id",
                schema: "identity",
                table: "email_intents");

            migrationBuilder.DropColumn(
                name: "lease_until",
                schema: "identity",
                table: "email_intents");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "identity",
                table: "email_intents");
        }
    }
}
