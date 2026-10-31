using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreserveAccountCurrentListRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserCurrentListRevisions",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCurrentListRevisions", x => x.UserAccountId);
                    table.CheckConstraint("CK_UserCurrentListRevisions_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_UserCurrentListRevisions_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.Sql("""
                LOCK TABLE "UserCurrentLists" IN SHARE MODE;
                INSERT INTO "UserCurrentListRevisions" ("UserAccountId", "Version", "UpdatedDate")
                    SELECT "UserAccountId", "Version", "UpdatedDate" FROM "UserCurrentLists";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "UserCurrentListRevisions" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "UserCurrentListRevisions") THEN
                        RAISE EXCEPTION 'Cannot remove current-list revision history; old selection versions could be reused. Perform an explicitly reviewed recovery';
                    END IF;
                END; $body$;
                """);
            migrationBuilder.DropTable(
                name: "UserCurrentListRevisions");
        }
    }
}
