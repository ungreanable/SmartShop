using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Ordering.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_orders_shop_id_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "shop_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_shop_id_status_open",
                schema: "ordering",
                table: "orders",
                columns: new[] { "shop_id", "status" },
                filter: "closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_shop_id_placed_at",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_shop_id_status_open",
                schema: "ordering",
                table: "orders");
        }
    }
}
