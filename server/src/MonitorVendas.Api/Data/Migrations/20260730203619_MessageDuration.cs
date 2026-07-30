using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "messages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "messages");
        }
    }
}
