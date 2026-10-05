using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartShop.Modules.Shops.Data.Migrations
{
    /// <inheritdoc />
    public partial class SuspendedShopVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "visible_while_suspended",
                schema: "shops",
                table: "shops",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "visible_while_suspended",
                schema: "shops",
                table: "shops");
        }
    }
}
