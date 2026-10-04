using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Shops.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shops");

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "shops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applicant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    house_no = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sample_image_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "favorites",
                schema: "shops",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_favorites", x => new { x.user_id, x.shop_id });
                });

            migrationBuilder.CreateTable(
                name: "invites",
                schema: "shops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shops",
                schema: "shops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    logo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cover_id = table.Column<Guid>(type: "uuid", nullable: true),
                    house_no = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    line_contact = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    suspend_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    override_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    override_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    busy_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vacation_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vacation_message = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    accept_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    accept_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    reminder_after_minutes = table.Column<int>(type: "integer", nullable: false),
                    auto_complete_hours = table.Column<int>(type: "integer", nullable: false),
                    allow_preorder_when_closed = table.Column<bool>(type: "boolean", nullable: false),
                    prep_time_minutes = table.Column<int>(type: "integer", nullable: false),
                    slot_interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    require_payment_before_preparing = table.Column<bool>(type: "boolean", nullable: false),
                    pickup_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pickup_instruction = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    delivery_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    delivery_zone_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    delivery_min_order = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    rating_average = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    rating_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    hours = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shops", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "status_trackers",
                schema: "shops",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_announced_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    next_evaluation_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_trackers", x => x.shop_id);
                });

            migrationBuilder.CreateTable(
                name: "closures",
                schema: "shops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_closures", x => x.id);
                    table.ForeignKey(
                        name: "fk_closures_shops_shop_id",
                        column: x => x.shop_id,
                        principalSchema: "shops",
                        principalTable: "shops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "members",
                schema: "shops",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    receive_order_notifications = table.Column<bool>(type: "boolean", nullable: false),
                    display_label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_members", x => new { x.shop_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_members_shops_shop_id",
                        column: x => x.shop_id,
                        principalSchema: "shops",
                        principalTable: "shops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_methods",
                schema: "shops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    prompt_pay_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    qr_image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instructions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requires_proof = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_methods", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_methods_shops_shop_id",
                        column: x => x.shop_id,
                        principalSchema: "shops",
                        principalTable: "shops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applications_applicant_id",
                schema: "shops",
                table: "applications",
                column: "applicant_id");

            migrationBuilder.CreateIndex(
                name: "ix_applications_plant_id_status",
                schema: "shops",
                table: "applications",
                columns: new[] { "plant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_closures_shop_id",
                schema: "shops",
                table: "closures",
                column: "shop_id");

            migrationBuilder.CreateIndex(
                name: "ix_favorites_shop_id",
                schema: "shops",
                table: "favorites",
                column: "shop_id");

            migrationBuilder.CreateIndex(
                name: "ix_invites_code",
                schema: "shops",
                table: "invites",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_members_user_id",
                schema: "shops",
                table: "members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_shop_id",
                schema: "shops",
                table: "payment_methods",
                column: "shop_id");

            migrationBuilder.CreateIndex(
                name: "ix_shops_plant_id_code",
                schema: "shops",
                table: "shops",
                columns: new[] { "plant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applications",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "closures",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "favorites",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "invites",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "members",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "payment_methods",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "status_trackers",
                schema: "shops");

            migrationBuilder.DropTable(
                name: "shops",
                schema: "shops");
        }
    }
}
