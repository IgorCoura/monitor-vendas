using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WarmupGenerationBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GenerationFailures",
                table: "warmup_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "GenerationPausedUntil",
                table: "warmup_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastGenerationError",
                table: "warmup_settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerationFailures",
                table: "warmup_settings");

            migrationBuilder.DropColumn(
                name: "GenerationPausedUntil",
                table: "warmup_settings");

            migrationBuilder.DropColumn(
                name: "LastGenerationError",
                table: "warmup_settings");
        }
    }
}
