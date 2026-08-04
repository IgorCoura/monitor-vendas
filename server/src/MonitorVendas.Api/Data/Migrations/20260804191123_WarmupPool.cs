using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WarmupPool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warmup_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeerAId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerBId = table.Column<Guid>(type: "uuid", nullable: false),
                    Theme = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedA = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedB = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "warmup_peers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WhatsappNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Persona = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_peers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_warmup_peers_whatsapp_numbers_WhatsappNumberId",
                        column: x => x.WhatsappNumberId,
                        principalTable: "whatsapp_numbers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "warmup_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    HaltedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HaltReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "warmup_turns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    FromPeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WaMessageId = table.Column<string>(type: "text", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_turns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_warmup_turns_warmup_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "warmup_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "warmup_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerAId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerBId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConversationsPerWeek = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastConversationAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_warmup_links_warmup_peers_PeerAId",
                        column: x => x.PeerAId,
                        principalTable: "warmup_peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_warmup_links_warmup_peers_PeerBId",
                        column: x => x.PeerBId,
                        principalTable: "warmup_peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_warmup_conversations_CreatedAt",
                table: "warmup_conversations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_warmup_conversations_Status",
                table: "warmup_conversations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_warmup_links_PeerAId_PeerBId",
                table: "warmup_links",
                columns: new[] { "PeerAId", "PeerBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warmup_links_PeerBId",
                table: "warmup_links",
                column: "PeerBId");

            migrationBuilder.CreateIndex(
                name: "IX_warmup_peers_WhatsappNumberId",
                table: "warmup_peers",
                column: "WhatsappNumberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warmup_turns_ConversationId",
                table: "warmup_turns",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_warmup_turns_SentAt_ScheduledAt",
                table: "warmup_turns",
                columns: new[] { "SentAt", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_warmup_turns_WaMessageId",
                table: "warmup_turns",
                column: "WaMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warmup_links");

            migrationBuilder.DropTable(
                name: "warmup_settings");

            migrationBuilder.DropTable(
                name: "warmup_turns");

            migrationBuilder.DropTable(
                name: "warmup_peers");

            migrationBuilder.DropTable(
                name: "warmup_conversations");
        }
    }
}
