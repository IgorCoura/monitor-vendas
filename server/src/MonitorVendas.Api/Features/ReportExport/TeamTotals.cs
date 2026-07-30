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

        var withSamples = all.Where(m => m.ResponseSamplesCount > 0).ToList();
        var samples = all.Sum(m => m.ResponseSamplesCount);

        return new MetricsDto(
            started,
            answered,
            all.Sum(m => m.ConversationsUnanswered),
            all.Sum(m => m.OutboundConversationsStarted),
            all.Sum(m => m.OutboundConversationsEngaged),
            Rate(answered, started),
            null,
            samples > 0 ? withSamples.Sum(m => (m.AvgResponseMinutes ?? 0) * m.ResponseSamplesCount) / samples : null,
            withSamples.Count > 0 ? withSamples.Min(m => m.MinResponseMinutes) : null,
            withSamples.Count > 0 ? withSamples.Max(m => m.MaxResponseMinutes) : null,
            samples,
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
            // Uptime é a única taxa que pode ser somada por média simples: o
            // denominador é o período, igual para todo vendedor.
            all.Average(m => m.UptimePercent),
            all.Sum(m => m.BanCount),
            Outcomes(all, answered));
    }

    private static IReadOnlyList<OutcomeMetricDto> Outcomes(List<MetricsDto> all, int answered) =>
        [.. all.SelectMany(m => m.Outcomes)
            .GroupBy(o => o.TypeCode)
            .Select(group =>
            {
                var count = group.Sum(o => o.Count);
                return new OutcomeMetricDto(group.Key, group.First().Name, count, Rate(count, answered), null);
            })];

    private static double? Rate(int part, int total) => total > 0 ? (double)part / total : null;

    private static MetricsDto Empty() =>
        new(0, 0, 0, 0, 0, null, null, null, null, null, 0, 0, 0, null, null, null, 0, null, null, null, null, 0, null, 0, 0, []);
}
