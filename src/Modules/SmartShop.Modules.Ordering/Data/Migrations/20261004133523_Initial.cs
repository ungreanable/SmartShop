using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Ordering.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ordering");

            migrationBuilder.CreateTable(
                name: "carts",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_shop = table.Column<bool>(type: "boolean", nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_counters",
                schema: "ordering",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    last = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_counters", x => new { x.shop_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_no = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    fulfillment_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scheduled_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    discount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    promotion_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    payment_method_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_method_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_method_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payment_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    pre_order_round_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    cancel_request_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_photo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_complete_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    delivery_address = table.Column<string>(type: "jsonb", nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: true),
                    timeline = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_carts_user_id_plant_id",
                schema: "ordering",
                table: "carts",
                columns: new[] { "user_id", "plant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_order_id_sent_at",
                schema: "ordering",
                table: "messages",
                columns: new[] { "order_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_id_idempotency_key",
                schema: "ordering",
                table: "orders",
                columns: new[] { "customer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_id_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "customer_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_plant_id_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "plant_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_pre_order_round_id",
                schema: "ordering",
                table: "orders",
                column: "pre_order_round_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_shop_id_order_no",
                schema: "ordering",
                table: "orders",
                columns: new[] { "shop_id", "order_no" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_shop_id_status_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "shop_id", "status", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reports_plant_id_status",
                schema: "ordering",
                table: "reports",
                columns: new[] { "plant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carts",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "order_counters",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "ordering");
        }
    }
}
