using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations;

public partial class AddAccountEmailChange : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_ExternalAuthFlow_Purpose", "ExternalAuthFlows");
        migrationBuilder.AddCheckConstraint("CK_ExternalAuthFlow_Purpose", "ExternalAuthFlows",
            "\"Purpose\" IN ('signin','link','reauthenticate','email','email_change') AND \"ExpiresAt\">\"CreatedAt\" AND ((\"UserAccountId\" IS NULL) = (\"UserSessionId\" IS NULL))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "ExternalAuthFlows" WHERE "Purpose"='email_change') THEN
                    RAISE EXCEPTION 'Email-change records exist. Rollback would discard verification history; use a reviewed roll-forward instead.';
                END IF;
            END $$;
            """);
        migrationBuilder.DropCheckConstraint("CK_ExternalAuthFlow_Purpose", "ExternalAuthFlows");
        migrationBuilder.AddCheckConstraint("CK_ExternalAuthFlow_Purpose", "ExternalAuthFlows",
            "\"Purpose\" IN ('signin','link','reauthenticate','email') AND \"ExpiresAt\">\"CreatedAt\" AND ((\"UserAccountId\" IS NULL) = (\"UserSessionId\" IS NULL))");
    }
}
