using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UserAccounts_Credentials",
                table: "UserAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmailOutbox_Kind",
                table: "EmailOutboxMessages");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReauthenticatedAt",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAt",
                table: "UserAccounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalAuthFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: true),
                    UserSessionId = table.Column<long>(type: "bigint", nullable: true),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAuthFlows", x => x.Id);
                    table.CheckConstraint("CK_ExternalAuthFlow_Purpose", "\"Purpose\" IN ('signin','link','reauthenticate','email') AND \"ExpiresAt\">\"CreatedAt\" AND ((\"UserAccountId\" IS NULL) = (\"UserSessionId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_ExternalAuthFlows_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalAuthFlows_UserSessions_Owner",
                        columns: x => new { x.UserSessionId, x.UserAccountId },
                        principalTable: "UserSessions",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    ProviderEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.CheckConstraint("CK_ExternalIdentity_Provider", "\"Provider\" IN ('google','facebook','microsoft') AND length(\"Subject\") BETWEEN 1 AND 512");
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserAccounts_Credentials",
                table: "UserAccounts",
                sql: "(\"IsTrial\" AND \"ExpiresDate\" IS NOT NULL AND \"Email\" IS NULL AND \"PasswordHash\" IS NULL AND \"PasswordSalt\" IS NULL) OR (NOT \"IsTrial\" AND ((\"PasswordHash\" IS NULL AND \"PasswordSalt\" IS NULL) OR (\"Email\" IS NOT NULL AND \"PasswordHash\" IS NOT NULL AND \"PasswordSalt\" IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmailOutbox_Kind",
                table: "EmailOutboxMessages",
                sql: "\"Kind\" IN ('account_recovery','password_changed','security_alert','signin_email')");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthFlows_ExpiresAt",
                table: "ExternalAuthFlows",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthFlows_UserAccountId_ExpiresAt",
                table: "ExternalAuthFlows",
                columns: new[] { "UserAccountId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthFlows_UserSessionId_UserAccountId",
                table: "ExternalAuthFlows",
                columns: new[] { "UserSessionId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Provider_Subject",
                table: "ExternalIdentities",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_ProviderEmail",
                table: "ExternalIdentities",
                column: "ProviderEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserAccountId_Provider",
                table: "ExternalIdentities",
                columns: new[] { "UserAccountId", "Provider" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION social_identity_immutable() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF (NEW."Id",NEW."UserAccountId",NEW."Provider",NEW."Subject") IS DISTINCT FROM
                       (OLD."Id",OLD."UserAccountId",OLD."Provider",OLD."Subject") THEN
                        RAISE EXCEPTION 'External identity ownership is immutable';
                    END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER social_identity_immutable BEFORE UPDATE ON "ExternalIdentities"
                    FOR EACH ROW EXECUTE FUNCTION social_identity_immutable();

                CREATE FUNCTION social_require_credential() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE account_id bigint; trial boolean; password text;
                BEGIN
                    IF TG_TABLE_NAME='UserAccounts' THEN account_id=NEW."Id";
                    ELSIF TG_OP='DELETE' THEN account_id=OLD."UserAccountId";
                    ELSE account_id=NEW."UserAccountId"; END IF;
                    SELECT "IsTrial","PasswordHash" INTO trial,password FROM "UserAccounts" WHERE "Id"=account_id FOR UPDATE;
                    IF NOT FOUND THEN RETURN NULL; END IF;
                    IF trial AND EXISTS(SELECT 1 FROM "ExternalIdentities" WHERE "UserAccountId"=account_id) THEN
                        RAISE EXCEPTION 'Trial accounts cannot have external identities';
                    END IF;
                    IF NOT trial AND password IS NULL AND NOT EXISTS(SELECT 1 FROM "ExternalIdentities" WHERE "UserAccountId"=account_id) THEN
                        RAISE EXCEPTION 'A permanent account needs a usable sign-in method';
                    END IF;
                    RETURN NULL;
                END; $body$;
                CREATE CONSTRAINT TRIGGER social_account_credential AFTER INSERT OR UPDATE ON "UserAccounts"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION social_require_credential();
                CREATE CONSTRAINT TRIGGER social_identity_credential AFTER INSERT OR UPDATE OR DELETE ON "ExternalIdentities"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION social_require_credential();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "ExternalIdentities","ExternalAuthFlows","UserAccounts","UserSessions","EmailOutboxMessages" IN ACCESS EXCLUSIVE MODE;
                DO $body$ BEGIN
                    IF EXISTS(SELECT 1 FROM "ExternalIdentities") OR EXISTS(SELECT 1 FROM "ExternalAuthFlows")
                       OR EXISTS(SELECT 1 FROM "UserAccounts" WHERE "EmailVerifiedAt" IS NOT NULL OR (NOT "IsTrial" AND "PasswordHash" IS NULL))
                       OR EXISTS(SELECT 1 FROM "UserSessions" WHERE "ReauthenticatedAt" IS NOT NULL)
                       OR EXISTS(SELECT 1 FROM "EmailOutboxMessages" WHERE "Kind"='signin_email') THEN
                        RAISE EXCEPTION 'Social sign-in data exists; use a reviewed recovery or roll-forward';
                    END IF;
                END; $body$;
                DROP TRIGGER social_account_credential ON "UserAccounts";
                DROP FUNCTION social_require_credential() CASCADE;
                DROP FUNCTION social_identity_immutable() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "ExternalAuthFlows");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_UserAccounts_Credentials",
                table: "UserAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmailOutbox_Kind",
                table: "EmailOutboxMessages");

            migrationBuilder.DropColumn(
                name: "ReauthenticatedAt",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAt",
                table: "UserAccounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserAccounts_Credentials",
                table: "UserAccounts",
                sql: "(\"IsTrial\" AND \"ExpiresDate\" IS NOT NULL AND \"Email\" IS NULL AND \"PasswordHash\" IS NULL AND \"PasswordSalt\" IS NULL) OR (NOT \"IsTrial\" AND \"Email\" IS NOT NULL AND \"PasswordHash\" IS NOT NULL AND \"PasswordSalt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmailOutbox_Kind",
                table: "EmailOutboxMessages",
                sql: "\"Kind\" IN ('account_recovery','password_changed','security_alert')");
        }
    }
}
