using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SendPauseAndBanCooldown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BannedUntil",
                table: "whatsapp_numbers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SendingPauseReason",
                table: "whatsapp_numbers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendingPausedUntil",
                table: "whatsapp_numbers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BannedUntil",
                table: "whatsapp_numbers");

            migrationBuilder.DropColumn(
                name: "SendingPauseReason",
                table: "whatsapp_numbers");

            migrationBuilder.DropColumn(
                name: "SendingPausedUntil",
                table: "whatsapp_numbers");
        }
    }
}
