namespace MonitorVendas.Api.Features.Metrics;

public static class ReportsEndpoints
{
    public static RouteGroupBuilder MapReportsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/reports/sellers/{sellerId:guid}", async (
            Guid sellerId,
            DateTime? from,
            DateTime? to,
            HttpRequest request,
            ReportQueries queries,
            ReportCache cache,
            CancellationToken ct) =>
        {
            var (fromUtc, toUtc) = NormalizeRange(from, to);
            if (fromUtc >= toUtc)
                return InvalidRange();

            var report = await cache.GetOrCreateAsync(
                $"seller:{sellerId}:{fromUtc:O}:{toUtc:O}",
                WantsFreshData(request),
                () => queries.GetSellerReportAsync(sellerId, fromUtc, toUtc, ct));

            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        group.MapGet("/reports/ranking", async (
            DateTime? from,
            DateTime? to,
            HttpRequest request,
            ReportQueries queries,
            ReportCache cache,
            CancellationToken ct) =>
        {
            var (fromUtc, toUtc) = NormalizeRange(from, to);
            if (fromUtc >= toUtc)
                return InvalidRange();

            var ranking = await cache.GetOrCreateAsync<IReadOnlyList<RankingEntryDto>>(
                $"ranking:{fromUtc:O}:{toUtc:O}",
                WantsFreshData(request),
                async () => await queries.GetRankingAsync(fromUtc, toUtc, ct));

            return Results.Ok(ranking);
        });

        // Refaz o agregado diário de um intervalo. Necessário depois de mudar o
        // horário comercial (config) ou corrigir dados antigos; o cadastro de
        // feriado já dispara isso sozinho.
        group.MapPost("/reports/rebuild", async (
            DateOnly? from,
            DateOnly? to,
            DailyMetricsBuilder builder,
            ReportCacheVersion cacheVersion,
            CancellationToken ct) =>
        {
            var toDay = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var fromDay = from ?? toDay.AddDays(-90);
            if (fromDay > toDay)
                return InvalidRange();

            var marked = await builder.MarkRangeDirtyAsync(fromDay, toDay, ct);
            var processed = await builder.ProcessDirtyDaysAsync(int.MaxValue, ct);
            cacheVersion.Bump();

            return Results.Ok(new { from = fromDay, to = toDay, marked, processed });
        });

        return group;
    }

    // A atualização manual do painel manda `Cache-Control: no-cache` — quem clica
    // no botão espera número recalculado, não o do cache.
    private static bool WantsFreshData(HttpRequest request) =>
        request.Headers.CacheControl.Any(v => v is not null && v.Contains("no-cache", StringComparison.OrdinalIgnoreCase));

    private static IResult InvalidRange() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["'from' precisa ser anterior a 'to'."],
        });

    // Sem parâmetros: últimos 30 dias. Datas sem offset são tratadas como UTC.
    private static (DateTime FromUtc, DateTime ToUtc) NormalizeRange(DateTime? from, DateTime? to)
    {
        var toUtc = ToUtc(to) ?? DateTime.UtcNow;
        var fromUtc = ToUtc(from) ?? toUtc.AddDays(-30);
        return (fromUtc, toUtc);
    }

    private static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };
}
