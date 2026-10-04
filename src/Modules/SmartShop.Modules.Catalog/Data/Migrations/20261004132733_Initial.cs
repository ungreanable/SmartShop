using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inventories",
                schema: "catalog",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false),
                    daily_quota = table.Column<int>(type: "integer", nullable: true),
                    daily_reset_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    last_reset_date = table.Column<DateOnly>(type: "date", nullable: true),
                    low_stock_threshold = table.Column<int>(type: "integer", nullable: true),
                    low_stock_notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventories", x => x.item_id);
                    table.CheckConstraint("ck_inventory_non_negative", "reserved >= 0 AND on_hand >= reserved");
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    stock_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    image_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    is_sold_out = table.Column<bool>(type: "boolean", nullable: false),
                    sale_from = table.Column<DateOnly>(type: "date", nullable: true),
                    sale_to = table.Column<DateOnly>(type: "date", nullable: true),
                    max_per_order = table.Column<int>(type: "integer", nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    slot_capacity = table.Column<int>(type: "integer", nullable: true),
                    allowed_fulfillment = table.Column<int[]>(type: "integer[]", nullable: true),
                    pre_order_round_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_recommended = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    announced_sold_out = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    availability_windows = table.Column<string>(type: "jsonb", nullable: true),
                    modifier_groups = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "preorder_rounds",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    order_cutoff = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fulfill_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fulfill_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    min_total_quantity = table.Column<int>(type: "integer", nullable: true),
                    ordered_quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preorder_rounds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "slot_bookings",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    slot_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slot_bookings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    on_hand_after = table.Column<int>(type: "integer", nullable: false),
                    reserved_after = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_shop_id",
                schema: "catalog",
                table: "categories",
                column: "shop_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_plant_id",
                schema: "catalog",
                table: "items",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_pre_order_round_id",
                schema: "catalog",
                table: "items",
                column: "pre_order_round_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_shop_id_deleted_at",
                schema: "catalog",
                table: "items",
                columns: new[] { "shop_id", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_preorder_rounds_shop_id_status",
                schema: "catalog",
                table: "preorder_rounds",
                columns: new[] { "shop_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_slot_bookings_item_id_slot_start",
                schema: "catalog",
                table: "slot_bookings",
                columns: new[] { "item_id", "slot_start" });

            migrationBuilder.CreateIndex(
                name: "ix_slot_bookings_order_id_item_id",
                schema: "catalog",
                table: "slot_bookings",
                columns: new[] { "order_id", "item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_item_id_at",
                schema: "catalog",
                table: "stock_movements",
                columns: new[] { "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_order_id_item_id_type",
                schema: "catalog",
                table: "stock_movements",
                columns: new[] { "order_id", "item_id", "type" },
                unique: true,
                filter: "order_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "inventories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "items",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "preorder_rounds",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "slot_bookings",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "stock_movements",
                schema: "catalog");
        }
    }
}
