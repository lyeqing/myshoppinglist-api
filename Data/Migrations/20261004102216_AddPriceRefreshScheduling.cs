using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceRefreshScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "ColesExtensionTasks",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshNotBefore",
                table: "ColesExtensionTasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ColesExtensionTasks");

            migrationBuilder.DropColumn(
                name: "RefreshNotBefore",
                table: "ColesExtensionTasks");
        }
    }
}
