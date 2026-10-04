using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartShop.SharedKernel.Scheduling;

#nullable disable

namespace SmartShop.Modules.Search.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "search");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "item_listings",
                schema: "search",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_sold_out = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_listings", x => x.item_id);
                });

            migrationBuilder.CreateTable(
                name: "shop_listings",
                schema: "search",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    logo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cover_id = table.Column<Guid>(type: "uuid", nullable: true),
                    house_no = table.Column<string>(type: "text", nullable: true),
                    pickup_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    delivery_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    allow_preorder = table.Column<bool>(type: "boolean", nullable: false),
                    prep_time_minutes = table.Column<int>(type: "integer", nullable: false),
                    rating_average = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    rating_count = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    schedule = table.Column<ShopScheduleSnapshot>(type: "jsonb", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_listings", x => x.shop_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_listings_name",
                schema: "search",
                table: "item_listings",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_item_listings_plant_id_shop_id",
                schema: "search",
                table: "item_listings",
                columns: new[] { "plant_id", "shop_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shop_listings_plant_id",
                schema: "search",
                table: "shop_listings",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_listings_search_text",
                schema: "search",
                table: "shop_listings",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_listings",
                schema: "search");

            migrationBuilder.DropTable(
                name: "shop_listings",
                schema: "search");
        }
    }
}
