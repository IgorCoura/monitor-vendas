using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.Warmup;

// O dia do aquecimento é o dia LOCAL, não o UTC. A meta diária, a janela de
// envio e a contagem do que já saiu precisam concordar sobre onde o dia começa:
// em UTC o contador zeraria às 21h de Brasília, no meio da janela da noite, e o
// pool tentaria despejar uma cota inteira na última hora.
public sealed class WarmupClock(IOptions<MetricsOptions> options)
{
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime LocalNow(DateTime utcNow) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeZone);

    public DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(LocalNow(utcNow));

    public DateTime DayStartUtc(DateTime utcNow) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(LocalNow(utcNow).Date, DateTimeKind.Unspecified), TimeZone);
}
