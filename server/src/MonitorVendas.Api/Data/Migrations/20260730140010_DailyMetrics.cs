using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_number_metrics",
                columns: table => new
                {
                    WhatsappNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    ConversationsStarted = table.Column<int>(type: "integer", nullable: false),
                    ConversationsAnswered = table.Column<int>(type: "integer", nullable: false),
                    OutboundConversationsStarted = table.Column<int>(type: "integer", nullable: false),
                    OutboundConversationsEngaged = table.Column<int>(type: "integer", nullable: false),
                    MessagesSent = table.Column<int>(type: "integer", nullable: false),
                    MessagesReceived = table.Column<int>(type: "integer", nullable: false),
                    OutboundRead = table.Column<int>(type: "integer", nullable: false),
                    SilenceGaps = table.Column<int>(type: "integer", nullable: false),
                    SilenceGapsFollowedUp = table.Column<int>(type: "integer", nullable: false),
                    Sales = table.Column<int>(type: "integer", nullable: false),
                    BanCount = table.Column<int>(type: "integer", nullable: false),
                    ResponseCount = table.Column<int>(type: "integer", nullable: false),
                    ResponseMinutesSum = table.Column<double>(type: "double precision", nullable: false),
                    ResponseMinutesMin = table.Column<double>(type: "double precision", nullable: true),
                    ResponseMinutesMax = table.Column<double>(type: "double precision", nullable: true),
                    TimeToCloseCount = table.Column<int>(type: "integer", nullable: false),
                    TimeToCloseHoursSum = table.Column<double>(type: "double precision", nullable: false),
                    EffectiveBusinessHours = table.Column<double>(type: "double precision", nullable: false),
                    DowntimeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    LastOutboundMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstResponseHistogram = table.Column<int[]>(type: "integer[]", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_number_metrics", x => new { x.WhatsappNumberId, x.Day });
                    table.ForeignKey(
                        name: "FK_daily_number_metrics_whatsapp_numbers_WhatsappNumberId",
                        column: x => x.WhatsappNumberId,
                        principalTable: "whatsapp_numbers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dirty_metrics_days",
                columns: table => new
                {
                    WhatsappNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    MarkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dirty_metrics_days", x => new { x.WhatsappNumberId, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_daily_number_metrics_Day",
                table: "daily_number_metrics",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_number_metrics");

            migrationBuilder.DropTable(
                name: "dirty_metrics_days");
        }
    }
}
