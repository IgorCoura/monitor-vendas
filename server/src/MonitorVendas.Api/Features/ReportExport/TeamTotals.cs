using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

// Totais do time pelas mesmas regras do painel: contagens somam, taxas são
// recalculadas a partir das somas (média de taxas seria errada) e a espera média
// é ponderada pela quantidade de amostras.
//
// O que não dá para reconstruir a partir do DTO por vendedor — mediana, taxa de
// leitura, follow-up, tempo até fechar — sai como nulo e aparece na planilha como "—".
// Estimar aqui seria inventar número onde o painel não inventa.
public static class TeamTotals
{
    public static MetricsDto Of(IReadOnlyList<RankingEntryDto> ranking)
    {
        var all = ranking.Select(r => r.Metrics).ToList();
        if (all.Count == 0)
            return Empty();

        var started = all.Sum(m => m.ConversationsStarted);
        var answered = all.Sum(m => m.ConversationsAnswered);
        var sales = all.Sum(m => m.Sales);
        var sent = all.Sum(m => m.MessagesSent);
        var received = all.Sum(m => m.MessagesReceived);
        var hours = all.Sum(m => m.EffectiveBusinessHours);

        // Espera de resposta: os dias de TODOS os vendedores são combinados dia a
        // dia e só então viram média, exatamente como no relatório de um vendedor.
        // Ponderar as médias já prontas por quantidade de amostras dava um terceiro
        // número, que não era o de ninguém.
        var responseDays = ResponseDays(all);

        var covered = all.Sum(m => m.UptimeCoveredSeconds);
        var downtime = all.Sum(m => m.UptimeDowntimeSeconds);

        return new MetricsDto(
            started,
            answered,
            all.Sum(m => m.ConversationsUnanswered),
            all.Sum(m => m.OutboundConversationsStarted),
            all.Sum(m => m.OutboundConversationsEngaged),
            Rate(answered, started),
            null,
            responseDays.Count == 0 ? null : responseDays.Average(d => d.SumMinutes / d.Count),
            responseDays.Count == 0 ? null : responseDays.Min(d => d.MinMinutes),
            responseDays.Count == 0 ? null : responseDays.Max(d => d.MaxMinutes),
            responseDays.Sum(d => d.Count),
            responseDays,
            sent,
            received,
            received > 0 ? (double)sent / received : null,
            null,
            null,
            sales,
            Rate(sales, answered),
            null,
            hours > 0 ? sent / hours : null,
            hours > 0 ? received / hours : null,
            hours,
            all.Max(m => m.LastOutboundMessageAt),
            // Uptime também é taxa recalculada das somas: média simples contaria
            // vendedor sem número (uptime nulo) como se fosse 100% e puxaria o
            // time para cima. O denominador é canal-segundos, não o período.
            Uptime(covered, downtime),
            covered,
            downtime,
            all.Sum(m => m.BanCount),
            Outcomes(all, answered));
    }

    // Um dia é um dia, não um dia por vendedor: as consolidações do mesmo dia se
    // juntam antes de virar média, senão o dia entraria uma vez por vendedor.
    private static IReadOnlyList<ResponseWaitDayDto> ResponseDays(List<MetricsDto> all) =>
        [.. all.SelectMany(m => m.ResponseWaitDays)
            .GroupBy(d => d.Day)
            .OrderBy(g => g.Key)
            .Select(g => new ResponseWaitDayDto(
                g.Key,
                g.Sum(d => d.Count),
                g.Sum(d => d.SumMinutes),
                g.Min(d => d.MinMinutes),
                g.Max(d => d.MaxMinutes)))];

    private static IReadOnlyList<OutcomeMetricDto> Outcomes(List<MetricsDto> all, int answered) =>
        [.. all.SelectMany(m => m.Outcomes)
            .GroupBy(o => o.TypeCode)
            .Select(group =>
            {
                var count = group.Sum(o => o.Count);
                return new OutcomeMetricDto(group.Key, group.First().Name, count, Rate(count, answered), null);
            })];

    private static double? Rate(int part, int total) => total > 0 ? (double)part / total : null;

    private static double? Uptime(double coveredSeconds, double downtimeSeconds) => coveredSeconds > 0
        ? Math.Clamp((coveredSeconds - downtimeSeconds) / coveredSeconds * 100, 0, 100)
        : null;

    private static MetricsDto Empty() =>
        new(0, 0, 0, 0, 0, null, null, null, null, null, 0, [], 0, 0, null, null, null, 0, null, null, null, null, 0, null, null, 0, 0, 0, []);
}
