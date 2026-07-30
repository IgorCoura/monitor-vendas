using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisHistoryAndSynthesisCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversation_ai_analyses_ConversationId",
                table: "conversation_ai_analyses");

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "conversation_ai_analyses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // As análises que já existiam SÃO as correntes. Sem este backfill elas
            // ficam com IsCurrent = false e somem da tela e da planilha, que
            // filtram por essa coluna — o histórico engoliria o presente.
            migrationBuilder.Sql("""UPDATE conversation_ai_analyses SET "IsCurrent" = TRUE;""");

            migrationBuilder.CreateTable(
                name: "seller_ai_syntheses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    InputsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Overview = table.Column<string>(type: "text", nullable: true),
                    Strengths = table.Column<string>(type: "text", nullable: true),
                    Improvements = table.Column<string>(type: "text", nullable: true),
                    DominantLossPattern = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TrainingSuggestion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CostBrl = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ConversationsCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_ai_syntheses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_seller_ai_syntheses_sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_ai_analyses_ConversationId",
                table: "conversation_ai_analyses",
                column: "ConversationId",
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_ai_analyses_ConversationId_AnalyzedAt",
                table: "conversation_ai_analyses",
                columns: new[] { "ConversationId", "AnalyzedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_ai_syntheses_SellerId_InputsHash",
                table: "seller_ai_syntheses",
                columns: new[] { "SellerId", "InputsHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seller_ai_syntheses");

            migrationBuilder.DropIndex(
                name: "IX_conversation_ai_analyses_ConversationId",
                table: "conversation_ai_analyses");

            migrationBuilder.DropIndex(
                name: "IX_conversation_ai_analyses_ConversationId_AnalyzedAt",
                table: "conversation_ai_analyses");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "conversation_ai_analyses");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_ai_analyses_ConversationId",
                table: "conversation_ai_analyses",
                column: "ConversationId",
                unique: true);
        }
    }
}
