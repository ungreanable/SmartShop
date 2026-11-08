using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoundOrderLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "round_orders",
                schema: "catalog",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    released = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_round_orders", x => new { x.order_id, x.round_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_round_orders_round_id",
                schema: "catalog",
                table: "round_orders",
                column: "round_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "round_orders",
                schema: "catalog");
        }
    }
}
