using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferredShopLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserPreferredShopLocations",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ShopLocationId = table.Column<long>(type: "bigint", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferredShopLocations", x => new { x.UserAccountId, x.ShopLocationId });
                    table.CheckConstraint("CK_UserPreferredShopLocations_Order", "\"DisplayOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_UserPreferredShopLocations_ShopLocations_ShopLocationId",
                        column: x => x.ShopLocationId,
                        principalTable: "ShopLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserPreferredShopLocations_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferredShopLocations_ShopLocationId",
                table: "UserPreferredShopLocations",
                column: "ShopLocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPreferredShopLocations");
        }
    }
}
