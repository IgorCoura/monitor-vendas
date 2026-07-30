using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConversationAiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_ai_analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StatusConfidence = table.Column<double>(type: "double precision", nullable: false),
                    StatusEvidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LossReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AskedForSale = table.Column<bool>(type: "boolean", nullable: false),
                    IgnoredBuyingSignal = table.Column<bool>(type: "boolean", nullable: false),
                    Objections = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ShouldRecontact = table.Column<bool>(type: "boolean", nullable: false),
                    RecontactReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SuggestedMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Interest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConductAlert = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CostBrl = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_ai_analyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_ai_analyses_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_ai_analyses_ConversationId",
                table: "conversation_ai_analyses",
                column: "ConversationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_ai_analyses");
        }
    }
}
