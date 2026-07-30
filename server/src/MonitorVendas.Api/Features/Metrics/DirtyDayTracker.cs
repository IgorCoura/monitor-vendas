using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Metrics;

public interface IDirtyDayTracker
{
    Task MarkAsync(AppDbContext db, Guid numberId, DateTime occurredAtUtc, CancellationToken ct);
}

// O pipeline de webhooks só SINALIZA que um dia precisa ser refeito — nenhuma regra
// de métrica é duplicada aqui. O INSERT participa da transação do processador de
// webhooks, então marca e efeito são atômicos.
//
// Marca também os dias anteriores (janela de resposta): uma resposta enviada hoje
// altera o número de ontem, onde estava a pergunta sem resposta.
public sealed class DirtyDayTracker(IOptions<MetricsOptions> options) : IDirtyDayTracker
{
    private const int LOOKBACK_DAYS = 2;

    public async Task MarkAsync(AppDbContext db, Guid numberId, DateTime occurredAtUtc, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var localDay = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), tz));

        var now = DateTime.UtcNow;
        for (var offset = 0; offset <= LOOKBACK_DAYS; offset++)
        {
            var day = localDay.AddDays(-offset);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO dirty_metrics_days ("WhatsappNumberId", "Day", "MarkedAt")
                VALUES ({numberId}, {day}, {now})
                ON CONFLICT DO NOTHING
                """,
                ct);
        }
    }
}
