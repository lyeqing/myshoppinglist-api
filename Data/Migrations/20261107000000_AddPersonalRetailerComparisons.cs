using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable
namespace myshoppinglist_api.Data.Migrations;

public partial class AddPersonalRetailerComparisons : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PersonalRetailerComparisons",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                CaptureJobId = table.Column<long>(type: "bigint", nullable: false),
                StateJson = table.Column<string>(type: "text", nullable: false),
                CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonalRetailerComparisons", x => x.Id);
                table.ForeignKey("FK_PersonalRetailerComparisons_ProductImportJobs_CaptureJobId", x => x.CaptureJobId,
                    "ProductImportJobs", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PersonalRetailerComparisons_UserAccounts_UserAccountId", x => x.UserAccountId,
                    "UserAccounts", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_PersonalRetailerComparisons_CaptureJobId", "PersonalRetailerComparisons", "CaptureJobId", unique: true);
        migrationBuilder.CreateIndex("IX_PersonalRetailerComparisons_UserAccountId", "PersonalRetailerComparisons", "UserAccountId");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PersonalRetailerComparisons");
}
