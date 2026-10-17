using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedShopLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectoryKey",
                table: "ShopLocations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceReference",
                table: "ShopLocations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "ShopLocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedByAccountId",
                table: "ShopLocations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShopLocationReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShopLocationId = table.Column<long>(type: "bigint", nullable: false),
                    ReviewerAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    BeforeJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    AfterJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopLocationReviews", x => x.Id);
                    table.CheckConstraint("CK_ShopLocationReviews_Action", "\"Action\" IN ('Created','Updated','Retired','Reactivated')");
                    table.CheckConstraint("CK_ShopLocationReviews_Evidence", "length(btrim(\"Evidence\")) > 0 AND length(btrim(\"SourceReference\")) > 0");
                    table.ForeignKey(
                        name: "FK_ShopLocationReviews_ShopLocations_ShopLocationId",
                        column: x => x.ShopLocationId,
                        principalTable: "ShopLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopLocationReviews_UserAccounts_ReviewerAccountId",
                        column: x => x.ReviewerAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocations_ShopId_DirectoryKey",
                table: "ShopLocations",
                columns: new[] { "ShopId", "DirectoryKey" },
                unique: true,
                filter: "\"DirectoryKey\" IS NOT NULL AND \"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocations_ShopId_IsActive_Postcode",
                table: "ShopLocations",
                columns: new[] { "ShopId", "IsActive", "Postcode" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocations_VerifiedByAccountId",
                table: "ShopLocations",
                column: "VerifiedByAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopLocations_Verification",
                table: "ShopLocations",
                sql: "\"VerifiedAt\" IS NULL AND \"VerifiedByAccountId\" IS NULL AND \"SourceReference\" IS NULL AND \"DirectoryKey\" IS NULL OR \"VerifiedAt\" IS NOT NULL AND \"VerifiedByAccountId\" IS NOT NULL AND length(btrim(\"SourceReference\")) > 0 AND \"SourceReference\" IS NOT NULL AND \"DirectoryKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocationReviews_ReviewerAccountId",
                table: "ShopLocationReviews",
                column: "ReviewerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocationReviews_ShopLocationId_Id",
                table: "ShopLocationReviews",
                columns: new[] { "ShopLocationId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_ShopLocations_UserAccounts_VerifiedByAccountId",
                table: "ShopLocations",
                column: "VerifiedByAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShopLocations_UserAccounts_VerifiedByAccountId",
                table: "ShopLocations");

            migrationBuilder.DropTable(
                name: "ShopLocationReviews");

            migrationBuilder.DropIndex(
                name: "IX_ShopLocations_ShopId_DirectoryKey",
                table: "ShopLocations");

            migrationBuilder.DropIndex(
                name: "IX_ShopLocations_ShopId_IsActive_Postcode",
                table: "ShopLocations");

            migrationBuilder.DropIndex(
                name: "IX_ShopLocations_VerifiedByAccountId",
                table: "ShopLocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopLocations_Verification",
                table: "ShopLocations");

            migrationBuilder.DropColumn(
                name: "DirectoryKey",
                table: "ShopLocations");

            migrationBuilder.DropColumn(
                name: "SourceReference",
                table: "ShopLocations");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "ShopLocations");

            migrationBuilder.DropColumn(
                name: "VerifiedByAccountId",
                table: "ShopLocations");
        }
    }
}
