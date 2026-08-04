using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveWarmup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarmupCompletedAt",
                table: "whatsapp_numbers");

            migrationBuilder.DropColumn(
                name: "WarmupPausedAt",
                table: "whatsapp_numbers");

            migrationBuilder.DropColumn(
                name: "WarmupStartedAt",
                table: "whatsapp_numbers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WarmupCompletedAt",
                table: "whatsapp_numbers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WarmupPausedAt",
                table: "whatsapp_numbers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WarmupStartedAt",
                table: "whatsapp_numbers",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
