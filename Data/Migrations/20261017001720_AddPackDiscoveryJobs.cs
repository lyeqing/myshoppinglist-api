using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPackDiscoveryJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackDiscoveryJobs",
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
                    table.PrimaryKey("PK_PackDiscoveryJobs", x => x.Id);
                    table.CheckConstraint("CK_PackDiscoveryJobs_Claim", "(\"Status\" = 'Processing' AND \"ClaimToken\" IS NOT NULL AND \"LeaseExpiresDate\" IS NOT NULL) OR (\"Status\" <> 'Processing' AND \"ClaimToken\" IS NULL AND \"LeaseExpiresDate\" IS NULL)");
                    table.CheckConstraint("CK_PackDiscoveryJobs_Counts", "\"Attempts\" >= 0 AND \"FoundCount\" >= 0");
                    table.CheckConstraint("CK_PackDiscoveryJobs_Status", "\"Status\" IN ('Queued','Processing','WaitingForExtension','Completed','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_PackDiscoveryJobs_ShoppingListProducts_ShoppingListProductI~",
                        columns: x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId },
                        principalTable: "ShoppingListProducts",
                        principalColumns: new[] { "Id", "ShoppingListId", "ProductId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackDiscoveryJobs_ShoppingLists_ShoppingListId_UserAccountId",
                        columns: x => new { x.ShoppingListId, x.UserAccountId },
                        principalTable: "ShoppingLists",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackDiscoveryJobs_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PackDiscoveryJobs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscoveryJobs_ShopId",
                table: "PackDiscoveryJobs",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscoveryJobs_ShoppingListId_UserAccountId",
                table: "PackDiscoveryJobs",
                columns: new[] { "ShoppingListId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscoveryJobs_ShoppingListProductId_ShoppingListId_Prod~",
                table: "PackDiscoveryJobs",
                columns: new[] { "ShoppingListProductId", "ShoppingListId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscoveryJobs_Status_NextAttemptDate",
                table: "PackDiscoveryJobs",
                columns: new[] { "Status", "NextAttemptDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscoveryJobs_UserAccountId_ShoppingListProductId_ShopId",
                table: "PackDiscoveryJobs",
                columns: new[] { "UserAccountId", "ShoppingListProductId", "ShopId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackDiscoveryJobs");
        }
    }
}
