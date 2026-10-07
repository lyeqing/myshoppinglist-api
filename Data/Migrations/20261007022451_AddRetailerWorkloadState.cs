using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetailerWorkloadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetailerWorkloadStates",
                columns: table => new
                {
                    Retailer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BlockedAt = table.Column<DateTime[]>(type: "timestamp with time zone[]", nullable: false),
                    PausedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProbeToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ProbeExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailerWorkloadStates", x => x.Retailer);
                    table.CheckConstraint("CK_RetailerWorkloadStates_Probe", "(\"ProbeToken\" IS NULL AND \"ProbeExpiresAt\" IS NULL) OR (\"ProbeToken\" IS NOT NULL AND \"ProbeExpiresAt\" IS NOT NULL AND \"PausedUntil\" IS NOT NULL)");
                    table.CheckConstraint("CK_RetailerWorkloadStates_Retailer", "\"Retailer\" IN ('coles', 'woolworths')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RetailerWorkloadStates");
        }
    }
}
