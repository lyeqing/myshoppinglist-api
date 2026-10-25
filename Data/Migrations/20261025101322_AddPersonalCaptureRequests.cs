using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalCaptureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptureRequestHash",
                table: "ProductImportJobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CaptureRequestId",
                table: "ProductImportJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CaptureResponseJson",
                table: "ProductImportJobs",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_UserAccountId_CaptureRequestId",
                table: "ProductImportJobs",
                columns: new[] { "UserAccountId", "CaptureRequestId" },
                unique: true,
                filter: "\"CaptureRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ProductImportJobs" WHERE "CaptureRequestId" IS NOT NULL
                        OR "CaptureRequestHash" IS NOT NULL OR "CaptureResponseJson" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove capture receipts while capture requests exist; restore a backup or migrate them explicitly';
                    END IF;
                END;
                $body$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_ProductImportJobs_UserAccountId_CaptureRequestId",
                table: "ProductImportJobs");

            migrationBuilder.DropColumn(
                name: "CaptureRequestHash",
                table: "ProductImportJobs");

            migrationBuilder.DropColumn(
                name: "CaptureRequestId",
                table: "ProductImportJobs");

            migrationBuilder.DropColumn(
                name: "CaptureResponseJson",
                table: "ProductImportJobs");

        }
    }
}
