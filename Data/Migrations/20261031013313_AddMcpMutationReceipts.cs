using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpMutationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpMutationReceipts",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpMutationReceipts", x => new { x.UserAccountId, x.RequestId });
                    table.ForeignKey(
                        name: "FK_McpMutationReceipts_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpMutationReceipts_CreatedDate",
                table: "McpMutationReceipts",
                column: "CreatedDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "McpMutationReceipts" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "McpMutationReceipts") THEN
                        RAISE EXCEPTION 'Cannot remove MCP mutation receipts; doing so can duplicate retried changes. Perform an explicitly reviewed recovery';
                    END IF;
                END; $body$;
                """);
            migrationBuilder.DropTable(
                name: "McpMutationReceipts");
        }
    }
}
