using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.ReportExport;

public sealed record ReportExportRequestBody(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<Guid>? SellerIds,
    IReadOnlyList<string>? Metrics,
    IReadOnlyList<string>? Charts,
    bool? IncludeNumbers,
    bool? IncludeAi);

public sealed record ReportExportDto(
    Guid Id,
    DateTime From,
    DateTime To,
    string Status,
    int TotalConversations,
    int AnalyzedConversations,
    int CachedConversations,
    int SkippedConversations,
    decimal CostBrl,
    string? Phase,
    string? Error,
    string? FileName,
    bool FileAvailable,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public static ReportExportDto Of(ReportExport export) =>
        new(export.Id, export.From, export.To, export.Status.ToString(),
            export.TotalConversations, export.AnalyzedConversations, export.CachedConversations,
            export.SkippedConversations, export.CostBrl, export.Phase, export.Error, export.FileName,
            export.File is not null, export.CreatedAt, export.CompletedAt);
}

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

        // Prévia de custo: é o que permite a tela mostrar "R$ 2,40 de R$ 47,60"
        // antes de o usuário confirmar.
        group.MapPost("/reports/export/estimate", async (
            ReportExportRequestBody body,
            IReportExportRunner runner,
            CancellationToken ct) =>
        {
            var request = Build(body);
            if (request is null)
                return InvalidRange();

            return Results.Ok(await runner.EstimateAsync(request, ct));
        });

        group.MapPost("/reports/export", async (
            ReportExportRequestBody body,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var request = Build(body);
            if (request is null)
                return InvalidRange();

            var export = new ReportExport
            {
                Id = Guid.NewGuid(),
                From = request.From,
                To = request.To,
                // Os filtros ficam congelados: o que roda em background é o que foi
                // confirmado na tela, mesmo que a seleção mude depois.
                FiltersJson = JsonSerializer.Serialize(request),
                Status = ReportExportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };

            db.Add(export);
            await db.SaveChangesAsync(ct);

            return Results.Accepted($"/api/v1/reports/export/{export.Id}", ReportExportDto.Of(export));
        });

        group.MapGet("/reports/export/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var export = await db.Set<ReportExport>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            return export is null ? Results.NotFound() : Results.Ok(ReportExportDto.Of(export));
        });

        group.MapGet("/reports/export/{id:guid}/file", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var export = await db.Set<ReportExport>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (export is null)
                return Results.NotFound();

            if (export.File is null)
                return Results.Conflict(new
                {
                    error = export.Status == ReportExportStatus.Failed
                        ? export.Error ?? "A exportação falhou."
                        : "A planilha ainda não está pronta.",
                });

            return Results.File(export.File, ReportWorkbookWriter.ContentType, export.FileName ?? "relatorio.xlsx");
        });

        return group;
    }

    private static ReportExportRequest? Build(ReportExportRequestBody body)
    {
        var to = UtcDates.ToUtc(body.To) ?? DateTime.UtcNow;
        var from = UtcDates.ToUtc(body.From) ?? to.AddDays(-30);
        if (from >= to)
            return null;

        return new ReportExportRequest(
            from,
            to,
            body.SellerIds ?? [],
            body.Metrics ?? [],
            body.Charts ?? [],
            body.IncludeNumbers ?? true,
            body.IncludeAi ?? false);
    }

    private static IResult InvalidRange() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["'from' precisa ser anterior a 'to'."],
        });
}
