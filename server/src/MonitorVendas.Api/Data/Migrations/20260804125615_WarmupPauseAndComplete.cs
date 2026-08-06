using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WarmupPauseAndComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarmupCompletedAt",
                table: "whatsapp_numbers");

            migrationBuilder.DropColumn(
                name: "WarmupPausedAt",
                table: "whatsapp_numbers");
        }
    }
}
