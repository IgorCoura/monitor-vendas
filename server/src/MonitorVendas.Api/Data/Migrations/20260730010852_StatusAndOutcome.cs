using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StatusAndOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LabelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MarkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_outcomes_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "number_status_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WhatsappNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<int>(type: "integer", nullable: true),
                    ResultingStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_status_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_number_status_events_whatsapp_numbers_WhatsappNumberId",
                        column: x => x.WhatsappNumberId,
                        principalTable: "whatsapp_numbers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LabelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_labels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_outcomes_ConversationId",
                table: "conversation_outcomes",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_number_status_events_WhatsappNumberId_OccurredAt",
                table: "number_status_events",
                columns: new[] { "WhatsappNumberId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_labels_InstanceName_LabelId",
                table: "whatsapp_labels",
                columns: new[] { "InstanceName", "LabelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_outcomes");

            migrationBuilder.DropTable(
                name: "number_status_events");

            migrationBuilder.DropTable(
                name: "whatsapp_labels");
        }
    }
}
