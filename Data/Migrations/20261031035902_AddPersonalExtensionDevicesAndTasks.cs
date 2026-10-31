using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalExtensionDevicesAndTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_UserSessions_Id_UserAccountId",
                table: "UserSessions",
                columns: new[] { "Id", "UserAccountId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AiConnections_Id_UserAccountId",
                table: "AiConnections",
                columns: new[] { "Id", "UserAccountId" });

            migrationBuilder.CreateTable(
                name: "ExtensionDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    UserSessionId = table.Column<long>(type: "bigint", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtensionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExtensionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupportedRetailers = table.Column<string[]>(type: "text[]", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Interactive = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionDevices", x => x.Id);
                    table.UniqueConstraint("AK_ExtensionDevices_Id_UserAccountId", x => new { x.Id, x.UserAccountId });
                    table.ForeignKey(
                        name: "FK_ExtensionDevices_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtensionDevices_UserSessions_UserSessionId_UserAccountId",
                        columns: x => new { x.UserSessionId, x.UserAccountId },
                        principalTable: "UserSessions",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExtensionTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    AiConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Retailer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Query = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ShoppingListId = table.Column<long>(type: "bigint", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultJson = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                    CompletionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionTasks", x => x.Id);
                    table.CheckConstraint("CK_ExtensionTasks_Completion", "\"Status\"<>'completed' OR (\"ResultJson\" IS NOT NULL AND \"CompletionHash\" IS NOT NULL AND \"DeviceId\" IS NOT NULL)");
                    table.CheckConstraint("CK_ExtensionTasks_Expiry", "\"ExpiresAt\">\"CreatedAt\"");
                    table.CheckConstraint("CK_ExtensionTasks_Status", "\"Status\" IN ('queued','awaiting_user','acknowledged','running','completed','failed','expired','cancelled')");
                    table.CheckConstraint("CK_ExtensionTasks_Target", "(\"Type\"='capture' AND \"ShoppingListId\" IS NOT NULL AND \"ShoppingListId\">0 AND \"Quantity\">0) OR (\"Type\"='search' AND \"ShoppingListId\" IS NULL)");
                    table.CheckConstraint("CK_ExtensionTasks_Type", "\"Type\" IN ('capture','search')");
                    table.ForeignKey(
                        name: "FK_ExtensionTasks_AiConnections_AiConnectionId_UserAccountId",
                        columns: x => new { x.AiConnectionId, x.UserAccountId },
                        principalTable: "AiConnections",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtensionTasks_ExtensionDevices_DeviceId_UserAccountId",
                        columns: x => new { x.DeviceId, x.UserAccountId },
                        principalTable: "ExtensionDevices",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtensionTasks_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionDevices_TokenHash",
                table: "ExtensionDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionDevices_UserAccountId_InstallationId",
                table: "ExtensionDevices",
                columns: new[] { "UserAccountId", "InstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionDevices_UserAccountId_RevokedAt_LastSeenAt",
                table: "ExtensionDevices",
                columns: new[] { "UserAccountId", "RevokedAt", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionDevices_UserSessionId_UserAccountId",
                table: "ExtensionDevices",
                columns: new[] { "UserSessionId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTasks_AiConnectionId_UserAccountId",
                table: "ExtensionTasks",
                columns: new[] { "AiConnectionId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTasks_DeviceId_UserAccountId",
                table: "ExtensionTasks",
                columns: new[] { "DeviceId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTasks_UserAccountId_AiConnectionId_RequestId",
                table: "ExtensionTasks",
                columns: new[] { "UserAccountId", "AiConnectionId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTasks_UserAccountId_ShoppingListId",
                table: "ExtensionTasks",
                columns: new[] { "UserAccountId", "ShoppingListId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTasks_UserAccountId_Status_ExpiresAt",
                table: "ExtensionTasks",
                columns: new[] { "UserAccountId", "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "ExtensionDevices", "ExtensionTasks" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ExtensionDevices") OR EXISTS (SELECT 1 FROM "ExtensionTasks") THEN
                        RAISE EXCEPTION 'Cannot remove personal extension links or task history; disable the bridge and perform an explicitly reviewed recovery';
                    END IF;
                END; $body$;
                """);
            migrationBuilder.DropTable(
                name: "ExtensionTasks");

            migrationBuilder.DropTable(
                name: "ExtensionDevices");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_UserSessions_Id_UserAccountId",
                table: "UserSessions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AiConnections_Id_UserAccountId",
                table: "AiConnections");
        }
    }
}
