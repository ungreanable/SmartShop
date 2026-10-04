using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Notifications.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    p256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "line_friendships",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_friend = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_friendships", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "line_quota",
                schema: "notifications",
                columns: table => new
                {
                    month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    sent = table.Column<int>(type: "integer", nullable: false),
                    exhausted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_quota", x => x.month);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    priority = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    push_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    line_muted = table.Column<List<string>>(type: "text[]", nullable: false),
                    push_muted = table.Column<List<string>>(type: "text[]", nullable: false),
                    quiet_from = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_to = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_notification_id_channel",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "notification_id", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_endpoint",
                schema: "notifications",
                table: "devices",
                column: "endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_user_id",
                schema: "notifications",
                table: "devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_source_event_id_user_id",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "source_event_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_created_at",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_read_at",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "user_id", "read_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "devices",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "line_friendships",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "line_quota",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "notifications");
        }
    }
}
