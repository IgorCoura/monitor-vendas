using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UptimeCoveredSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CoveredSeconds",
                table: "daily_number_metrics",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            // Backfill obrigatório: CoveredSeconds é o denominador do uptime, e
            // linha antiga com zero devolveria "—" para todo o histórico agregado.
            // As linhas existentes fecham um dia local inteiro, que é o que elas já
            // usavam implicitamente como denominador. Dia com fuso irregular ou
            // número nascido no meio do dia fica com uma fração a mais — quem
            // quiser o valor exato roda POST /reports/rebuild no período.
            migrationBuilder.Sql("""UPDATE daily_number_metrics SET "CoveredSeconds" = 86400;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoveredSeconds",
                table: "daily_number_metrics");
        }
    }
}
