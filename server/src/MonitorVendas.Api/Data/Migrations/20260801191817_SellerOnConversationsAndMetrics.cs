using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SellerOnConversationsAndMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SellerId",
                table: "messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SellerId",
                table: "daily_number_metrics",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SellerId",
                table: "conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill obrigatório: sem ele todo o histórico nasce com SellerId
            // vazio e some dos relatórios — o dono de hoje é a melhor (e única)
            // informação que temos sobre o passado.
            migrationBuilder.Sql("""
                UPDATE conversations c
                SET "SellerId" = n."SellerId"
                FROM whatsapp_numbers n
                WHERE n."Id" = c."WhatsappNumberId";

                UPDATE messages m
                SET "SellerId" = n."SellerId"
                FROM whatsapp_numbers n
                WHERE n."Id" = m."WhatsappNumberId";

                UPDATE daily_number_metrics d
                SET "SellerId" = n."SellerId"
                FROM whatsapp_numbers n
                WHERE n."Id" = d."WhatsappNumberId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_messages_SellerId_Timestamp",
                table: "messages",
                columns: new[] { "SellerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_daily_number_metrics_SellerId_Day",
                table: "daily_number_metrics",
                columns: new[] { "SellerId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_SellerId_StartedAt",
                table: "conversations",
                columns: new[] { "SellerId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_SellerId_Timestamp",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_daily_number_metrics_SellerId_Day",
                table: "daily_number_metrics");

            migrationBuilder.DropIndex(
                name: "IX_conversations_SellerId_StartedAt",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "daily_number_metrics");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "conversations");
        }
    }
}
