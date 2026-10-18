using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserProductActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListProductId = table.Column<long>(type: "bigint", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurchasedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProductActivities", x => x.Id);
                    table.CheckConstraint("CK_UserProductActivities_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_UserProductActivities_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserProductActivities_ShoppingListProducts_ShoppingListProd~",
                        column: x => x.ShoppingListProductId,
                        principalTable: "ShoppingListProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserProductActivities_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserProductPreferences",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    IsEssential = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProductPreferences", x => new { x.UserAccountId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_UserProductPreferences_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserProductPreferences_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProductActivities_ProductId",
                table: "UserProductActivities",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_UserProductActivities_ShoppingListProductId",
                table: "UserProductActivities",
                column: "ShoppingListProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProductActivities_UserAccountId_ProductId",
                table: "UserProductActivities",
                columns: new[] { "UserAccountId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProductPreferences_ProductId",
                table: "UserProductPreferences",
                column: "ProductId");

            // Preserve existing activity without inventing records for previously deleted items.
            migrationBuilder.Sql("""
                INSERT INTO "UserProductActivities" ("UserAccountId", "ProductId", "ShoppingListProductId", "AddedDate", "PurchasedDate", "Quantity")
                SELECT l."UserAccountId", i."ProductId", i."Id", i."AddedDate",
                       CASE WHEN i."IsPurchased" THEN i."PurchasedDate" ELSE NULL END, i."Quantity"
                FROM "ShoppingListProducts" i
                JOIN "ShoppingLists" l ON l."Id" = i."ShoppingListId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserProductActivities");

            migrationBuilder.DropTable(
                name: "UserProductPreferences");
        }
    }
}
