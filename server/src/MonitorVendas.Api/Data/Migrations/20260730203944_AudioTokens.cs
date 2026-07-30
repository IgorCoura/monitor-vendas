using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AudioTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InputAudioTokens",
                table: "ai_usages",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputAudioTokens",
                table: "ai_usages");
        }
    }
}
