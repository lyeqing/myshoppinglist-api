using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandDiscoveryJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListProductId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FoundCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailedTaskId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandDiscoveryJobs", x => x.Id);
                    table.CheckConstraint("CK_BrandDiscoveryJobs_Claim", "(\"Status\" = 'Processing' AND \"ClaimToken\" IS NOT NULL AND \"LeaseExpiresDate\" IS NOT NULL) OR (\"Status\" <> 'Processing' AND \"ClaimToken\" IS NULL AND \"LeaseExpiresDate\" IS NULL)");
                    table.CheckConstraint("CK_BrandDiscoveryJobs_Counts", "\"Attempts\" >= 0 AND \"FoundCount\" >= 0");
                    table.CheckConstraint("CK_BrandDiscoveryJobs_Status", "\"Status\" IN ('Queued','Processing','WaitingForExtension','Completed','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_BrandDiscoveryJobs_ShoppingListProducts_ShoppingListProduct~",
                        columns: x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId },
                        principalTable: "ShoppingListProducts",
                        principalColumns: new[] { "Id", "ShoppingListId", "ProductId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BrandDiscoveryJobs_ShoppingLists_ShoppingListId_UserAccount~",
                        columns: x => new { x.ShoppingListId, x.UserAccountId },
                        principalTable: "ShoppingLists",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BrandDiscoveryJobs_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandDiscoveryJobs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductBrandSuggestions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BrandDiscoveryJobId = table.Column<long>(type: "bigint", nullable: false),
                    AlternativeProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    DiscoveredDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductBrandSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductBrandSuggestions_BrandDiscoveryJobs_BrandDiscoveryJo~",
                        column: x => x.BrandDiscoveryJobId,
                        principalTable: "BrandDiscoveryJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductBrandSuggestions_Products_AlternativeProductId",
                        column: x => x.AlternativeProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductBrandSuggestions_ShopProducts_ShopProductId",
                        column: x => x.ShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandDiscoveryJobs_ShopId",
                table: "BrandDiscoveryJobs",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_BrandDiscoveryJobs_ShoppingListId_UserAccountId",
                table: "BrandDiscoveryJobs",
                columns: new[] { "ShoppingListId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandDiscoveryJobs_ShoppingListProductId_ShoppingListId_Pro~",
                table: "BrandDiscoveryJobs",
                columns: new[] { "ShoppingListProductId", "ShoppingListId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandDiscoveryJobs_Status_NextAttemptDate",
                table: "BrandDiscoveryJobs",
                columns: new[] { "Status", "NextAttemptDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandDiscoveryJobs_UserAccountId_ShoppingListProductId_Shop~",
                table: "BrandDiscoveryJobs",
                columns: new[] { "UserAccountId", "ShoppingListProductId", "ShopId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductBrandSuggestions_AlternativeProductId",
                table: "ProductBrandSuggestions",
                column: "AlternativeProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductBrandSuggestions_BrandDiscoveryJobId_AlternativeProd~",
                table: "ProductBrandSuggestions",
                columns: new[] { "BrandDiscoveryJobId", "AlternativeProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductBrandSuggestions_ShopProductId",
                table: "ProductBrandSuggestions",
                column: "ShopProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductBrandSuggestions");

            migrationBuilder.DropTable(
                name: "BrandDiscoveryJobs");
        }
    }
}
