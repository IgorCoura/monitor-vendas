using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiJobActiveFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "ai_jobs",
                type: "boolean",
                nullable: true);

            // Job que atravessou a atualização não tem dono: nasceria com a flag
            // vazia e a tela não saberia que ele existe. Encerra todos antes de o
            // índice único passar a valer.
            migrationBuilder.Sql("""
                UPDATE ai_jobs
                SET "Status" = 'Failed',
                    "Error" = 'A rodada foi interrompida por uma atualização do sistema.',
                    "CompletedAt" = now()
                WHERE "Status" IN ('Pending', 'Running');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_Active",
                table: "ai_jobs",
                column: "Active",
                unique: true,
                filter: "\"Active\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_jobs_Active",
                table: "ai_jobs");

            migrationBuilder.DropColumn(
                name: "Active",
                table: "ai_jobs");
        }
    }
}
