using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerContributionsAndAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContributionBlocked",
                table: "UserAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ContributionEnabled",
                table: "UserAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "UserAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RestrictionReason",
                table: "UserAccounts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContributionObservationId",
                table: "ShopProductPrices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContributionObservationId",
                table: "ShopProductPriceHistory",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContributorAccountId",
                table: "ColesExtensionTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountAdminAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdministratorId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: false),
                    AfterJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAdminAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributionObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: true),
                    TaskId = table.Column<long>(type: "bigint", nullable: true),
                    ImportJobId = table.Column<long>(type: "bigint", nullable: true),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ExtensionVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContributionObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPrices_ContributionObservationId",
                table: "ShopProductPrices",
                column: "ContributionObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPriceHistory_ContributionObservationId",
                table: "ShopProductPriceHistory",
                column: "ContributionObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAdminAudits_AccountId_CreatedAt",
                table: "AccountAdminAudits",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContributionObservations_UserAccountId_ReceivedAt",
                table: "ContributionObservations",
                columns: new[] { "UserAccountId", "ReceivedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_ShopProductPriceHistory_ContributionObservations_Contributi~",
                table: "ShopProductPriceHistory",
                column: "ContributionObservationId",
                principalTable: "ContributionObservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopProductPrices_ContributionObservations_ContributionObse~",
                table: "ShopProductPrices",
                column: "ContributionObservationId",
                principalTable: "ContributionObservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShopProductPriceHistory_ContributionObservations_Contributi~",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopProductPrices_ContributionObservations_ContributionObse~",
                table: "ShopProductPrices");

            migrationBuilder.DropTable(
                name: "AccountAdminAudits");

            migrationBuilder.DropTable(
                name: "ContributionObservations");

            migrationBuilder.DropIndex(
                name: "IX_ShopProductPrices_ContributionObservationId",
                table: "ShopProductPrices");

            migrationBuilder.DropIndex(
                name: "IX_ShopProductPriceHistory_ContributionObservationId",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropColumn(
                name: "ContributionBlocked",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "ContributionEnabled",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "RestrictionReason",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "ContributionObservationId",
                table: "ShopProductPrices");

            migrationBuilder.DropColumn(
                name: "ContributionObservationId",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropColumn(
                name: "ContributorAccountId",
                table: "ColesExtensionTasks");
        }
    }
}
