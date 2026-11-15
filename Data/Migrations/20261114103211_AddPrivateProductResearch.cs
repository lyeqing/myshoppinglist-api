using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateProductResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Query = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WorkflowVersion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchJobs", x => x.Id);
                    table.UniqueConstraint("AK_ResearchJobs_Id_UserAccountId", x => new { x.Id, x.UserAccountId });
                    table.ForeignKey(
                        name: "FK_ResearchJobs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ResearchJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ReportJson = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    Validation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IssuesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SelectedProductId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchCandidates_Products_SelectedProductId",
                        column: x => x.SelectedProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResearchCandidates_ResearchJobs_ResearchJobId_UserAccountId",
                        columns: x => new { x.ResearchJobId, x.UserAccountId },
                        principalTable: "ResearchJobs",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ResearchJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Retailer = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Target = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchIssues", x => x.Id);
                    table.CheckConstraint("CK_ResearchIssue_Revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_ResearchIssues_ResearchJobs_ResearchJobId_UserAccountId",
                        columns: x => new { x.ResearchJobId, x.UserAccountId },
                        principalTable: "ResearchJobs",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCandidates_ResearchJobId_Key",
                table: "ResearchCandidates",
                columns: new[] { "ResearchJobId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCandidates_ResearchJobId_UserAccountId",
                table: "ResearchCandidates",
                columns: new[] { "ResearchJobId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCandidates_SelectedProductId",
                table: "ResearchCandidates",
                column: "SelectedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIssues_ResearchJobId_Retailer_Code_Target",
                table: "ResearchIssues",
                columns: new[] { "ResearchJobId", "Retailer", "Code", "Target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIssues_ResearchJobId_UserAccountId",
                table: "ResearchIssues",
                columns: new[] { "ResearchJobId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchJobs_UserAccountId_CreatedAt",
                table: "ResearchJobs",
                columns: new[] { "UserAccountId", "CreatedAt" });
            migrationBuilder.Sql("""
                CREATE FUNCTION research_candidate_guard() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW."Id", NEW."UserAccountId", NEW."ResearchJobId", NEW."Key", NEW."ReportJson", NEW."Validation", NEW."IssuesJson")
                        IS DISTINCT FROM (OLD."Id", OLD."UserAccountId", OLD."ResearchJobId", OLD."Key", OLD."ReportJson", OLD."Validation", OLD."IssuesJson") THEN
                        RAISE EXCEPTION 'Research evidence is immutable; submit a correction';
                    END IF;
                    IF NEW."SelectedProductId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Products" WHERE "Id" = NEW."SelectedProductId" AND "UserAccountId" = NEW."UserAccountId" AND NOT "IsDeleted") THEN
                        RAISE EXCEPTION 'Research selection must belong to the same account';
                    END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER research_candidate_guard BEFORE INSERT OR UPDATE ON "ResearchCandidates"
                    FOR EACH ROW EXECUTE FUNCTION research_candidate_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "ResearchJobs", "ResearchCandidates", "ResearchIssues" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ResearchJobs") OR EXISTS (SELECT 1 FROM "ResearchCandidates")
                        OR EXISTS (SELECT 1 FROM "ResearchIssues") THEN
                        RAISE EXCEPTION 'Cannot remove populated research evidence; disable entry points and use reviewed recovery';
                    END IF;
                END; $body$;
                DROP TRIGGER research_candidate_guard ON "ResearchCandidates";
                DROP FUNCTION research_candidate_guard();
                """);
            migrationBuilder.DropTable(
                name: "ResearchCandidates");

            migrationBuilder.DropTable(
                name: "ResearchIssues");

            migrationBuilder.DropTable(
                name: "ResearchJobs");
        }
    }
}
