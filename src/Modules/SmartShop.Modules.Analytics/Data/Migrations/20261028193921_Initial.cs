using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartShop.Modules.Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visitor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    custom = table.Column<bool>(type: "boolean", nullable: false),
                    repeat = table.Column<bool>(type: "boolean", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "item_daily",
                schema: "analytics",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    views = table.Column<int>(type: "integer", nullable: false),
                    viewers = table.Column<int>(type: "integer", nullable: false),
                    add_to_cart = table.Column<int>(type: "integer", nullable: false),
                    buyers = table.Column<int>(type: "integer", nullable: false),
                    ordered_quantity = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_daily", x => new { x.shop_id, x.item_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "shop_customers",
                schema: "analytics",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_order_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_customers", x => new { x.shop_id, x.customer_key });
                });

            migrationBuilder.CreateTable(
                name: "shop_daily",
                schema: "analytics",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitors = table.Column<int>(type: "integer", nullable: false),
                    shop_views = table.Column<int>(type: "integer", nullable: false),
                    item_viewers = table.Column<int>(type: "integer", nullable: false),
                    cart_adders = table.Column<int>(type: "integer", nullable: false),
                    cart_viewers = table.Column<int>(type: "integer", nullable: false),
                    buyers = table.Column<int>(type: "integer", nullable: false),
                    orders = table.Column<int>(type: "integer", nullable: false),
                    revenue = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    completed_orders = table.Column<int>(type: "integer", nullable: false),
                    completed_revenue = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    lost_orders = table.Column<int>(type: "integer", nullable: false),
                    abandoned_carts = table.Column<int>(type: "integer", nullable: false),
                    repeat_orders = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_daily", x => new { x.shop_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "shop_hourly",
                schema: "analytics",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    hour = table.Column<short>(type: "smallint", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    views = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_hourly", x => new { x.shop_id, x.day, x.hour });
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_occurred_at",
                schema: "analytics",
                table: "events",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_events_order_id_type_item_id",
                schema: "analytics",
                table: "events",
                columns: new[] { "order_id", "type", "item_id" },
                unique: true,
                filter: "order_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_events_shop_id_occurred_at",
                schema: "analytics",
                table: "events",
                columns: new[] { "shop_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_item_daily_shop_id_day",
                schema: "analytics",
                table: "item_daily",
                columns: new[] { "shop_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_shop_customers_customer_key",
                schema: "analytics",
                table: "shop_customers",
                column: "customer_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "item_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "shop_customers",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "shop_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "shop_hourly",
                schema: "analytics");
        }
    }
}
