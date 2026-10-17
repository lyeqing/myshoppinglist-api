using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedBrandAlternatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductAlternativeRelationships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    AlternativeProductId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousReviewId = table.Column<long>(type: "bigint", nullable: true),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SameUseAndFormatConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    DietaryAndCompositionChecked = table.Column<bool>(type: "boolean", nullable: false),
                    ProductFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AlternativeFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReviewerAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductAlternativeRelationships", x => x.Id);
                    table.CheckConstraint("CK_ProductAlternatives_Confirmation", "NOT \"Approved\" OR (\"SameUseAndFormatConfirmed\" AND \"DietaryAndCompositionChecked\")");
                    table.CheckConstraint("CK_ProductAlternatives_Evidence", "length(btrim(\"Evidence\")) > 0");
                    table.CheckConstraint("CK_ProductAlternatives_OrderedPair", "\"ProductId\" < \"AlternativeProductId\"");
                    table.ForeignKey(
                        name: "FK_ProductAlternativeRelationships_ProductAlternativeRelations~",
                        column: x => x.PreviousReviewId,
                        principalTable: "ProductAlternativeRelationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductAlternativeRelationships_Products_AlternativeProduct~",
                        column: x => x.AlternativeProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductAlternativeRelationships_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductAlternativeRelationships_UserAccounts_ReviewerAccoun~",
                        column: x => x.ReviewerAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductAlternativeRelationships_AlternativeProductId_Produc~",
                table: "ProductAlternativeRelationships",
                columns: new[] { "AlternativeProductId", "ProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductAlternativeRelationships_PreviousReviewId",
                table: "ProductAlternativeRelationships",
                column: "PreviousReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAlternativeRelationships_ProductId_AlternativeProdu~1",
                table: "ProductAlternativeRelationships",
                columns: new[] { "ProductId", "AlternativeProductId", "PreviousReviewId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_ProductAlternativeRelationships_ProductId_AlternativeProduc~",
                table: "ProductAlternativeRelationships",
                columns: new[] { "ProductId", "AlternativeProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductAlternativeRelationships_ReviewerAccountId",
                table: "ProductAlternativeRelationships",
                column: "ReviewerAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductAlternativeRelationships");
        }
    }
}
