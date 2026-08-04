using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Features.Metrics;

// Fecha o resumo de um dia por número reusando o MESMO MetricsCalculator do
// cálculo ao vivo — a regra vive num lugar só. Idempotente: recalcular o mesmo
// dia produz o mesmo resultado.
public sealed class DailyMetricsBuilder(
    AppDbContext db,
    ReportQueries queries,
    IOptions<MetricsOptions> options,
    ILogger<DailyMetricsBuilder> logger)
{
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateOnly LocalDayOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));

    public (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateOnly day)
    {
        var tz = TimeZone;
        var localStart = day.ToDateTime(TimeOnly.MinValue);
        return (
            TimeZoneInfo.ConvertTimeToUtc(localStart, tz),
            TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), tz));
    }

    public async Task<int> RebuildAsync(IEnumerable<(Guid NumberId, DateOnly Day)> targets, CancellationToken ct)
    {
        var list = targets.Distinct().ToList();
        if (list.Count == 0)
            return 0;

        var calculator = await queries.BuildCalculatorAsync(ct);
        var written = 0;

        foreach (var group in list.GroupBy(t => t.Day))
        {
            var numberIds = group.Select(t => t.NumberId).Distinct().ToArray();
            var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
                .Where(n => numberIds.Contains(n.Id))
                .ToListAsync(ct);

            if (numbers.Count == 0)
                continue;

            var (fromUtc, toUtc) = DayRangeUtc(group.Key);
            var results = await queries.ComputeForNumbersAsync(numbers, calculator, fromUtc, toUtc, ct);

            // O dono do dia sai do carimbo das próprias mensagens; dia sem mensagem
            // (só downtime, por exemplo) fica com o dono atual do número. A troca
            // de vendedor só vale a partir do dia seguinte, então o dia é de um só.
            // Pares distintos e agrupamento em memória: o Postgres não tem max(uuid),
            // e o dia tem um dono só — se aparecer mais de um, fica o primeiro.
            var sellerPairs = await db.Set<Conversations.Message>().AsNoTracking()
                .Where(m => numberIds.Contains(m.WhatsappNumberId) && m.Timestamp >= fromUtc && m.Timestamp < toUtc)
                .Select(m => new { m.WhatsappNumberId, m.SellerId })
                .Distinct()
                .ToListAsync(ct);

            var sellerByNumber = sellerPairs
                .GroupBy(p => p.WhatsappNumberId)
                .ToDictionary(g => g.Key, g => g.First().SellerId);

            var existing = await db.Set<DailyNumberMetrics>()
                .Where(d => numberIds.Contains(d.WhatsappNumberId) && d.Day == group.Key)
                .ToListAsync(ct);

            var existingOutcomes = await db.Set<DailyNumberOutcomeMetrics>()
                .Where(o => numberIds.Contains(o.WhatsappNumberId) && o.Day == group.Key)
                .ToListAsync(ct);

            // O agregado é por (número, dia) e o dia tem um dono só — a troca de
            // vendedor vale a partir do dia seguinte. Se ainda assim aparecer mais
            // de um par no mesmo dia, os pedaços somam em vez de um sobrescrever o
            // outro.
            foreach (var byNumber in results.GroupBy(kv => kv.Key.NumberId))
            {
                var numberId = byNumber.Key;

                var snapshot = MetricsSnapshot.Merge(byNumber.Select(kv => MetricsSnapshot.FromResult(kv.Value)));

                var row = existing.FirstOrDefault(d => d.WhatsappNumberId == numberId);
                if (row is null)
                {
                    row = new DailyNumberMetrics { WhatsappNumberId = numberId, Day = group.Key };
                    db.Add(row);
                }

                snapshot.CopyTo(row);
                row.SellerId = sellerByNumber.TryGetValue(numberId, out var seller)
                    ? seller
                    : numbers.First(n => n.Id == numberId).SellerId;
                row.ComputedAt = DateTime.UtcNow;
                written++;

                // Desfechos por tipo na tabela filha: o conjunto é reescrito, então
                // um tipo que deixou de ocorrer no dia desaparece corretamente.
                var previous = existingOutcomes.Where(o => o.WhatsappNumberId == numberId).ToList();
                foreach (var stale in previous.Where(p => !snapshot.Outcomes.ContainsKey(p.OutcomeTypeCode)))
                    db.Remove(stale);

                foreach (var (typeCode, totals) in snapshot.Outcomes)
                {
                    var outcomeRow = previous.FirstOrDefault(p => p.OutcomeTypeCode == typeCode);
                    if (outcomeRow is null)
                    {
                        outcomeRow = new DailyNumberOutcomeMetrics
                        {
                            WhatsappNumberId = numberId,
                            Day = group.Key,
                            OutcomeTypeCode = typeCode,
                        };
                        db.Add(outcomeRow);
                    }

                    outcomeRow.Count = totals.Count;
                    outcomeRow.TimeToCloseCount = totals.TimeToCloseCount;
                    outcomeRow.TimeToCloseHoursSum = totals.TimeToCloseHoursSum;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogDebug("Agregado diário atualizado: {Count} linhas.", written);
        return written;
    }

    // Marca um intervalo de dias para TODOS os números — usado pelo rebuild e por
    // mudanças que alteram o relógio útil retroativamente (feriado, horário).
    public async Task<int> MarkRangeDirtyAsync(DateOnly fromDay, DateOnly toDay, CancellationToken ct)
    {
        var numberIds = await db.Set<WhatsappNumber>().AsNoTracking().Select(n => n.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var marked = 0;

        foreach (var numberId in numberIds)
        {
            for (var day = fromDay; day <= toDay; day = day.AddDays(1))
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO dirty_metrics_days ("WhatsappNumberId", "Day", "MarkedAt")
                    VALUES ({numberId}, {day}, {now})
                    ON CONFLICT DO NOTHING
                    """,
                    ct);
                marked++;
            }
        }

        return marked;
    }

    // Consome as marcas deixadas pelo pipeline: refaz os dias e apaga as marcas.
    // Ordem importa — se falhar depois do rebuild, a marca continua e o dia é
    // refeito de novo (idempotente).
    public async Task<int> ProcessDirtyDaysAsync(int maxDays = 500, CancellationToken ct = default)
    {
        var dirty = await db.Set<DirtyMetricsDay>().AsNoTracking()
            .OrderBy(d => d.Day)
            .Take(maxDays)
            .ToListAsync(ct);

        if (dirty.Count == 0)
            return 0;

        await RebuildAsync(dirty.Select(d => (d.WhatsappNumberId, d.Day)), ct);

        foreach (var mark in dirty)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM dirty_metrics_days
                WHERE "WhatsappNumberId" = {mark.WhatsappNumberId} AND "Day" = {mark.Day}
                """,
                ct);
        }

        return dirty.Count;
    }
}

public sealed class MetricsAggregationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MetricsOptions> options,
    ILogger<MetricsAggregationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.AggregationIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var builder = scope.ServiceProvider.GetRequiredService<DailyMetricsBuilder>();
                var processed = await builder.ProcessDirtyDaysAsync(ct: stoppingToken);
                if (processed > 0)
                    logger.LogInformation("Agregação diária processou {Count} dias marcados.", processed);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ciclo de agregação de métricas.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
