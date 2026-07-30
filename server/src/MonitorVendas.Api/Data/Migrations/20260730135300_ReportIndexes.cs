using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_ConversationId",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId_Timestamp",
                table: "messages",
                columns: new[] { "ConversationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_WhatsappNumberId_StartedAt",
                table: "conversations",
                columns: new[] { "WhatsappNumberId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_ConversationId_Timestamp",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_WhatsappNumberId_StartedAt",
                table: "conversations");

            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId",
                table: "messages",
                column: "ConversationId");
        }
    }
}
