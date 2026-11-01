using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountRecoveryAndEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountCredentialChanges",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountCredentialChanges", x => x.UserAccountId);
                    table.ForeignKey(
                        name: "FK_AccountCredentialChanges_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecipientHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtectedPayload = table.Column<string>(type: "character varying(24000)", maxLength: 24000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_EmailOutbox_Kind", "\"Kind\" IN ('account_recovery','password_changed','security_alert')");
                    table.CheckConstraint("CK_EmailOutbox_Status", "\"Status\" IN ('queued','sending','sent','failed','expired','suppressed','cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutboxMessages_RecipientHash_CreatedAt",
                table: "EmailOutboxMessages",
                columns: new[] { "RecipientHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutboxMessages_Status_NextAttemptAt",
                table: "EmailOutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "EmailOutboxMessages", "AccountCredentialChanges" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "EmailOutboxMessages") OR EXISTS (SELECT 1 FROM "AccountCredentialChanges") THEN
                        RAISE EXCEPTION 'Cannot discard email or credential history; disable delivery/recovery and review rollback separately';
                    END IF;
                END; $body$;
                """);
            migrationBuilder.DropTable(
                name: "AccountCredentialChanges");

            migrationBuilder.DropTable(
                name: "EmailOutboxMessages");
        }
    }
}
