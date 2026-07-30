using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutcomeTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sales",
                table: "daily_number_metrics");

            migrationBuilder.DropColumn(
                name: "TimeToCloseCount",
                table: "daily_number_metrics");

            migrationBuilder.DropColumn(
                name: "TimeToCloseHoursSum",
                table: "daily_number_metrics");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeTypeCode",
                table: "conversation_outcomes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            // Preserva os desfechos existentes: "Sale" (texto livre antigo) vira o
            // código do tipo `sale` do catálogo novo. Só então a coluna antiga sai.
            migrationBuilder.Sql("""
                UPDATE conversation_outcomes
                SET "OutcomeTypeCode" = CASE WHEN lower("Kind") = 'sale' THEN 'sale' ELSE lower("Kind") END
                """);

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "conversation_outcomes");

            migrationBuilder.CreateTable(
                name: "conversation_labels",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LabelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LabelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_labels", x => new { x.ConversationId, x.LabelId });
                    table.ForeignKey(
                        name: "FK_conversation_labels_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_outcome_types",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_outcome_types", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "daily_number_outcome_metrics",
                columns: table => new
                {
                    WhatsappNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    OutcomeTypeCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    TimeToCloseCount = table.Column<int>(type: "integer", nullable: false),
                    TimeToCloseHoursSum = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_number_outcome_metrics", x => new { x.WhatsappNumberId, x.Day, x.OutcomeTypeCode });
                    table.ForeignKey(
                        name: "FK_daily_number_outcome_metrics_whatsapp_numbers_WhatsappNumbe~",
                        column: x => x.WhatsappNumberId,
                        principalTable: "whatsapp_numbers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outcome_label_terms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutcomeTypeCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Term = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outcome_label_terms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outcome_label_terms_conversation_outcome_types_OutcomeTypeC~",
                        column: x => x.OutcomeTypeCode,
                        principalTable: "conversation_outcome_types",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "conversation_outcome_types",
                columns: new[] { "Code", "Active", "Name", "SortOrder" },
                values: new object[,]
                {
                    { "lost", true, "Clientes perdidos", 2 },
                    { "sale", true, "Vendas", 1 }
                });

            migrationBuilder.InsertData(
                table: "outcome_label_terms",
                columns: new[] { "Id", "CreatedAt", "NormalizedKey", "OutcomeTypeCode", "Term" },
                values: new object[,]
                {
                    { new Guid("1a1e0001-0000-0000-0000-000000000001"), new DateTime(2026, 7, 30, 0, 0, 0, 0, DateTimeKind.Utc), "venda", "sale", "venda" },
                    { new Guid("1a1e0001-0000-0000-0000-000000000002"), new DateTime(2026, 7, 30, 0, 0, 0, 0, DateTimeKind.Utc), "perdido", "lost", "perdido" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_outcomes_OutcomeTypeCode_MarkedAt",
                table: "conversation_outcomes",
                columns: new[] { "OutcomeTypeCode", "MarkedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_labels_AppliedAt",
                table: "conversation_labels",
                column: "AppliedAt");

            migrationBuilder.CreateIndex(
                name: "IX_daily_number_outcome_metrics_Day_OutcomeTypeCode",
                table: "daily_number_outcome_metrics",
                columns: new[] { "Day", "OutcomeTypeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_outcome_label_terms_NormalizedKey",
                table: "outcome_label_terms",
                column: "NormalizedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outcome_label_terms_OutcomeTypeCode",
                table: "outcome_label_terms",
                column: "OutcomeTypeCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_labels");

            migrationBuilder.DropTable(
                name: "daily_number_outcome_metrics");

            migrationBuilder.DropTable(
                name: "outcome_label_terms");

            migrationBuilder.DropTable(
                name: "conversation_outcome_types");

            migrationBuilder.DropIndex(
                name: "IX_conversation_outcomes_OutcomeTypeCode_MarkedAt",
                table: "conversation_outcomes");

            migrationBuilder.DropColumn(
                name: "OutcomeTypeCode",
                table: "conversation_outcomes");

            migrationBuilder.AddColumn<int>(
                name: "Sales",
                table: "daily_number_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TimeToCloseCount",
                table: "daily_number_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "TimeToCloseHoursSum",
                table: "daily_number_metrics",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "conversation_outcomes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");
        }
    }
}
