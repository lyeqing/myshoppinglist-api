using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationsAndPrivateFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyOperationalSummaries",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Events = table.Column<long>(type: "bigint", nullable: false),
                    VisitorEstimate = table.Column<long>(type: "bigint", nullable: false),
                    Sessions = table.Column<long>(type: "bigint", nullable: false),
                    Accounts = table.Column<long>(type: "bigint", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyOperationalSummaries", x => new { x.Day, x.Component, x.Name, x.Outcome });
                });

            migrationBuilder.CreateTable(
                name: "FeedbackTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Release = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Screenshot = table.Column<byte[]>(type: "bytea", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Response = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IssueReference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackTickets", x => x.Id);
                    table.CheckConstraint("CK_FeedbackTickets_Category", "\"Category\" IN ('bug','suggestion','other')");
                    table.CheckConstraint("CK_FeedbackTickets_Rating", "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_FeedbackTickets_Screenshot", "\"Screenshot\" IS NULL OR octet_length(\"Screenshot\")<=500000");
                    table.CheckConstraint("CK_FeedbackTickets_Status", "\"Status\" IN ('new','triaged','planned','resolved','closed')");
                    table.ForeignKey(
                        name: "FK_FeedbackTickets_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OperationalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Release = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: true),
                    VisitorId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attribution = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationalEvents_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OperationalIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationsAdminAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdministratorId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationsAdminAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationsAdminAudits_UserAccounts_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentVersion = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RecipientCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_OperationalIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "OperationalIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_IncidentId_IncidentVersion_Kind",
                table: "AlertDeliveries",
                columns: new[] { "IncidentId", "IncidentVersion", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_Status_CreatedAt",
                table: "AlertDeliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackTickets_Status_CreatedAt",
                table: "FeedbackTickets",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackTickets_UserAccountId_RequestId",
                table: "FeedbackTickets",
                columns: new[] { "UserAccountId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEvents_CreatedAt",
                table: "OperationalEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEvents_Fingerprint_CreatedAt",
                table: "OperationalEvents",
                columns: new[] { "Fingerprint", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEvents_UserAccountId_CreatedAt",
                table: "OperationalEvents",
                columns: new[] { "UserAccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalIncidents_Fingerprint",
                table: "OperationalIncidents",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationsAdminAudits_AdministratorId",
                table: "OperationsAdminAudits",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationsAdminAudits_CreatedAt",
                table: "OperationsAdminAudits",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "AlertDeliveries", "DailyOperationalSummaries", "FeedbackTickets", "OperationalEvents", "OperationsAdminAudits", "OperationalIncidents" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "AlertDeliveries") OR EXISTS (SELECT 1 FROM "DailyOperationalSummaries")
                        OR EXISTS (SELECT 1 FROM "FeedbackTickets") OR EXISTS (SELECT 1 FROM "OperationalEvents")
                        OR EXISTS (SELECT 1 FROM "OperationsAdminAudits") OR EXISTS (SELECT 1 FROM "OperationalIncidents") THEN
                        RAISE EXCEPTION 'Cannot remove operational support, audit or incident history; disable operations and review recovery separately';
                    END IF;
                END; $body$;
                """);
            migrationBuilder.DropTable(
                name: "AlertDeliveries");

            migrationBuilder.DropTable(
                name: "DailyOperationalSummaries");

            migrationBuilder.DropTable(
                name: "FeedbackTickets");

            migrationBuilder.DropTable(
                name: "OperationalEvents");

            migrationBuilder.DropTable(
                name: "OperationsAdminAudits");

            migrationBuilder.DropTable(
                name: "OperationalIncidents");
        }
    }
}
