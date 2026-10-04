using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlipVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "system_verified",
                schema: "payments",
                table: "proofs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "system_verified_at",
                schema: "payments",
                table: "proofs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_message",
                schema: "payments",
                table: "proofs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "system_verified",
                schema: "payments",
                table: "proofs");

            migrationBuilder.DropColumn(
                name: "system_verified_at",
                schema: "payments",
                table: "proofs");

            migrationBuilder.DropColumn(
                name: "verification_message",
                schema: "payments",
                table: "proofs");
        }
    }
}
