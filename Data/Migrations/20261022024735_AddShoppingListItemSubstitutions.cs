using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShoppingListItemSubstitutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShoppingListItemSubstitutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListProductId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    ShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    OriginalFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AlternativeFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShoppingListItemSubstitutions", x => x.Id);
                    table.CheckConstraint("CK_ShoppingListItemSubstitutions_Quantity", "\"Quantity\" BETWEEN 1 AND 999");
                    table.ForeignKey(
                        name: "FK_ShoppingListItemSubstitutions_ShopProducts_ShopProductId",
                        column: x => x.ShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShoppingListItemSubstitutions_ShoppingListProducts_Shopping~",
                        columns: x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId },
                        principalTable: "ShoppingListProducts",
                        principalColumns: new[] { "Id", "ShoppingListId", "ProductId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShoppingListItemSubstitutions_ShoppingLists_ShoppingListId_~",
                        columns: x => new { x.ShoppingListId, x.UserAccountId },
                        principalTable: "ShoppingLists",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShoppingListItemSubstitutions_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItemSubstitutions_ShopId",
                table: "ShoppingListItemSubstitutions",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItemSubstitutions_ShoppingListId_UserAccountId",
                table: "ShoppingListItemSubstitutions",
                columns: new[] { "ShoppingListId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItemSubstitutions_ShoppingListProductId_ShopId",
                table: "ShoppingListItemSubstitutions",
                columns: new[] { "ShoppingListProductId", "ShopId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItemSubstitutions_ShoppingListProductId_Shoppin~",
                table: "ShoppingListItemSubstitutions",
                columns: new[] { "ShoppingListProductId", "ShoppingListId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItemSubstitutions_ShopProductId",
                table: "ShoppingListItemSubstitutions",
                column: "ShopProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShoppingListItemSubstitutions");
        }
    }
}
