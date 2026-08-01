using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.ReportExport;

public static class ReportExportEndpoints
{
    public static RouteGroupBuilder MapReportExportEndpoints(this RouteGroupBuilder group)
    {
        // A tela monta os filtros a partir daqui: tipo de desfecho novo vira opção
        // de coluna e de gráfico sem código no front.
        group.MapGet("/reports/export/metrics", async (AppDbContext db, CancellationToken ct) =>
        {
            var outcomes = await db.Set<Outcomes.ConversationOutcomeType>().AsNoTracking()
                .Where(t => t.Active)
                .OrderBy(t => t.SortOrder)
                .Select(t => new Metrics.OutcomeMetricDto(t.Code, t.Name, 0, null, null))
                .ToListAsync(ct);

            return Results.Ok(ReportMetricCatalog.Build(outcomes)
                .Select(m => new { m.Key, m.Label }));
        });

        // Download direto, como o de contatos: a planilha sai das métricas já
        // calculadas e leva milissegundos — não há o que esperar em background.
        group.MapGet("/reports/export", async (
            DateTime? from,
            DateTime? to,
            string? sellerIds,
            string? metrics,
            string? charts,
            bool? includeNumbers,
            ReportExportBuilder builder,
            CancellationToken ct) =>
        {
            var request = Build(from, to, sellerIds, metrics, charts, includeNumbers);
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["from"] = ["'from' precisa ser anterior a 'to'."],
                });

            var (file, fileName) = await builder.BuildAsync(request, ct);

            return Results.File(file, ReportWorkbookWriter.ContentType, fileName);
        });

        return group;
    }

    private static ReportExportRequest? Build(
        DateTime? from,
        DateTime? to,
        string? sellerIds,
        string? metrics,
        string? charts,
        bool? includeNumbers)
    {
        var toUtc = UtcDates.ToUtc(to) ?? DateTime.UtcNow;
        var fromUtc = UtcDates.ToUtc(from) ?? toUtc.AddDays(-30);
        if (fromUtc >= toUtc)
            return null;

        return new ReportExportRequest(
            fromUtc,
            toUtc,
            [.. Items(sellerIds)
                .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)],
            Items(metrics),
            Items(charts),
            includeNumbers ?? true);
    }

    // Listas vêm separadas por vírgula, como nos filtros de contatos.
    private static List<string> Items(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
