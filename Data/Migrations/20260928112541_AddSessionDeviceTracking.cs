using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionDeviceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "UserSessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceModel",
                table: "UserSessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "UserSessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenDate",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Latitude",
                table: "UserSessions",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LocationAccuracy",
                table: "UserSessions",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LocationCapturedDate",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Longitude",
                table: "UserSessions",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "UserSessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "UserSessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "UserSessions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserSessions_Latitude",
                table: "UserSessions",
                sql: "\"Latitude\" BETWEEN -90 AND 90");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserSessions_Longitude",
                table: "UserSessions",
                sql: "\"Longitude\" BETWEEN -180 AND 180");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UserSessions_Latitude",
                table: "UserSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_UserSessions_Longitude",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "DeviceModel",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "LastSeenDate",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "LocationAccuracy",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "LocationCapturedDate",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "UserSessions");
        }
    }
}
