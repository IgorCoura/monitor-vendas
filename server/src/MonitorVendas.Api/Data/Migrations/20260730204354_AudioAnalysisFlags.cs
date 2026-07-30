using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AudioAnalysisFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludedAudio",
                table: "conversation_ai_analyses",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludedAudio",
                table: "conversation_ai_analyses");
        }
    }
}
