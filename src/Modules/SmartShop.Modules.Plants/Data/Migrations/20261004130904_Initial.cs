using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Plants.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plants");

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "plants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    house_no = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    soi = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    nickname = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    request_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plants",
                schema: "plants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    join_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    picture_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    push_requirement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_plant_id_status",
                schema: "plants",
                table: "memberships",
                columns: new[] { "plant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_plant_id_user_id",
                schema: "plants",
                table: "memberships",
                columns: new[] { "plant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                schema: "plants",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_plants_join_code",
                schema: "plants",
                table: "plants",
                column: "join_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships",
                schema: "plants");

            migrationBuilder.DropTable(
                name: "plants",
                schema: "plants");
        }
    }
}
