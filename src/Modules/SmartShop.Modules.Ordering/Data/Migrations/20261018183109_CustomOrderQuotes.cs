using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Ordering.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomOrderQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "quote_message",
                schema: "ordering",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "quoted_by",
                schema: "ordering",
                table: "orders",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quote_message",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "quoted_by",
                schema: "ordering",
                table: "orders");
        }
    }
}
