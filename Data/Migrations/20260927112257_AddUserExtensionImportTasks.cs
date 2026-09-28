using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserExtensionImportTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserExtensionImportTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StepToken = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Query = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LinksJson = table.Column<string>(type: "text", nullable: false),
                    MatchesJson = table.Column<string>(type: "text", nullable: false),
                    HadFailures = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExtensionImportTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserExtensionImportTasks_ProductImportJobs_ProductImportJob~",
                        column: x => x.ProductImportJobId,
                        principalTable: "ProductImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserExtensionImportTasks_ProductImportJobId",
                table: "UserExtensionImportTasks",
                column: "ProductImportJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExtensionImportTasks_RequestId",
                table: "UserExtensionImportTasks",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserExtensionImportTasks");
        }
    }
}
