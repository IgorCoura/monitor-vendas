using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Api.Features.Metrics;

// Forma somável das métricas: é o que permite juntar dias fechados (vindos de
// DailyNumberMetrics + DailyNumberOutcomeMetrics) com o dia corrente (calculado
// ao vivo) e produzir um DTO.
public sealed record MetricsSnapshot(
    int ConversationsStarted,
    int ConversationsAnswered,
    int OutboundConversationsStarted,
    int OutboundConversationsEngaged,
    int MessagesSent,
    int MessagesReceived,
    int OutboundRead,
    int SilenceGaps,
    int SilenceGapsFollowedUp,
    IReadOnlyDictionary<string, OutcomeTotals> Outcomes,
    int BanCount,
    // Espera de resposta consolidada por dia. Não é uma soma só: mín, máx e média
    // do período saem de combinar os DIAS, e a média é a média das médias diárias.
    IReadOnlyDictionary<DateOnly, ResponseDayStats> ResponseByDay,
    double EffectiveBusinessHours,
    // Denominador do uptime: canal-segundos que este vendedor tinha no período.
    // Soma entre números e entre dias, e é o que faz 100% significar "todos os
    // números no ar o tempo todo" em vez de "nenhuma queda registrada".
    double CoveredSeconds,
    double DowntimeSeconds,
    DateTime? LastOutboundMessageAt,
    int[] FirstResponseHistogram,
    IReadOnlyList<double> FirstResponseSamples)
{
    public static MetricsSnapshot Empty => new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, OutcomeTotals>(StringComparer.Ordinal),
        0, new Dictionary<DateOnly, ResponseDayStats>(), 0, 0, 0, null, new int[FirstResponseBuckets.Count], []);

    // Do cálculo ao vivo (um dia ou um período inteiro).
    public static MetricsSnapshot FromResult(MetricsResult r)
    {
        var histogram = new int[FirstResponseBuckets.Count];
        foreach (var sample in r.FirstResponseMinutesSamples)
            histogram[FirstResponseBuckets.IndexOf(sample)]++;

        return new MetricsSnapshot(
            r.ConversationsStarted,
            r.ConversationsAnswered,
            r.OutboundConversationsStarted,
            r.OutboundConversationsEngaged,
            r.MessagesSent,
            r.MessagesReceived,
            r.OutboundRead,
            r.SilenceGaps,
            r.SilenceGapsFollowedUp,
            r.Outcomes,
            r.BanCount,
            r.ResponseByDay,
            r.EffectiveBusinessHours,
            r.CoveredSeconds,
            r.DowntimeSeconds,
            r.LastOutboundMessageAt,
            histogram,
            r.FirstResponseMinutesSamples);
    }

    public static MetricsSnapshot FromDaily(DailyNumberMetrics d, IEnumerable<DailyNumberOutcomeMetrics> outcomeRows) => new(
        d.ConversationsStarted,
        d.ConversationsAnswered,
        d.OutboundConversationsStarted,
        d.OutboundConversationsEngaged,
        d.MessagesSent,
        d.MessagesReceived,
        d.OutboundRead,
        d.SilenceGaps,
        d.SilenceGapsFollowedUp,
        outcomeRows.ToDictionary(
            o => o.OutcomeTypeCode,
            o => new OutcomeTotals(o.Count, o.TimeToCloseCount, o.TimeToCloseHoursSum),
            StringComparer.Ordinal),
        d.BanCount,
        // A linha do agregado JÁ é a consolidação daquele dia — vira uma entrada só.
        d.ResponseCount == 0
            ? new Dictionary<DateOnly, ResponseDayStats>()
            : new Dictionary<DateOnly, ResponseDayStats>
            {
                [d.Day] = new(d.ResponseCount, d.ResponseMinutesSum, d.ResponseMinutesMin ?? 0, d.ResponseMinutesMax ?? 0),
            },
        d.EffectiveBusinessHours,
        d.CoveredSeconds,
        d.DowntimeSeconds,
        d.LastOutboundMessageAt,
        d.FirstResponseHistogram.Length == FirstResponseBuckets.Count
            ? d.FirstResponseHistogram
            : new int[FirstResponseBuckets.Count],
        []);

    public void CopyTo(DailyNumberMetrics target)
    {
        target.ConversationsStarted = ConversationsStarted;
        target.ConversationsAnswered = ConversationsAnswered;
        target.OutboundConversationsStarted = OutboundConversationsStarted;
        target.OutboundConversationsEngaged = OutboundConversationsEngaged;
        target.MessagesSent = MessagesSent;
        target.MessagesReceived = MessagesReceived;
        target.OutboundRead = OutboundRead;
        target.SilenceGaps = SilenceGaps;
        target.SilenceGapsFollowedUp = SilenceGapsFollowedUp;
        target.BanCount = BanCount;

        // A linha é de um dia só, então normalmente há uma entrada. Combinar todas
        // em vez de pegar a primeira é o que garante que nada seja descartado em
        // silêncio se um dia isso deixar de valer.
        var day = ResponseByDay.Values.Aggregate((ResponseDayStats?)null, (acc, d) => acc is null ? d : acc.Plus(d));
        target.ResponseCount = day?.Count ?? 0;
        target.ResponseMinutesSum = day?.SumMinutes ?? 0;
        target.ResponseMinutesMin = day?.MinMinutes;
        target.ResponseMinutesMax = day?.MaxMinutes;
        target.EffectiveBusinessHours = EffectiveBusinessHours;
        target.CoveredSeconds = CoveredSeconds;
        target.DowntimeSeconds = DowntimeSeconds;
        target.LastOutboundMessageAt = LastOutboundMessageAt;
        target.FirstResponseHistogram = FirstResponseHistogram;
    }

    public static MetricsSnapshot Merge(IEnumerable<MetricsSnapshot> parts)
    {
        var histogram = new int[FirstResponseBuckets.Count];
        var samples = new List<double>();
        var outcomeParts = new List<IReadOnlyDictionary<string, OutcomeTotals>>();
        var responseParts = new List<IReadOnlyDictionary<DateOnly, ResponseDayStats>>();
        var acc = Empty;
        var any = false;

        foreach (var p in parts)
        {
            any = true;
            for (var i = 0; i < histogram.Length && i < p.FirstResponseHistogram.Length; i++)
                histogram[i] += p.FirstResponseHistogram[i];
            samples.AddRange(p.FirstResponseSamples);
            outcomeParts.Add(p.Outcomes);
            responseParts.Add(p.ResponseByDay);

            acc = acc with
            {
                ConversationsStarted = acc.ConversationsStarted + p.ConversationsStarted,
                ConversationsAnswered = acc.ConversationsAnswered + p.ConversationsAnswered,
                OutboundConversationsStarted = acc.OutboundConversationsStarted + p.OutboundConversationsStarted,
                OutboundConversationsEngaged = acc.OutboundConversationsEngaged + p.OutboundConversationsEngaged,
                MessagesSent = acc.MessagesSent + p.MessagesSent,
                MessagesReceived = acc.MessagesReceived + p.MessagesReceived,
                OutboundRead = acc.OutboundRead + p.OutboundRead,
                SilenceGaps = acc.SilenceGaps + p.SilenceGaps,
                SilenceGapsFollowedUp = acc.SilenceGapsFollowedUp + p.SilenceGapsFollowedUp,
                BanCount = acc.BanCount + p.BanCount,
                EffectiveBusinessHours = acc.EffectiveBusinessHours + p.EffectiveBusinessHours,
                CoveredSeconds = acc.CoveredSeconds + p.CoveredSeconds,
                DowntimeSeconds = acc.DowntimeSeconds + p.DowntimeSeconds,
                LastOutboundMessageAt = Later(acc.LastOutboundMessageAt, p.LastOutboundMessageAt),
            };
        }

        return any
            ? acc with
            {
                Outcomes = MetricsResult.MergeOutcomes(outcomeParts),
                ResponseByDay = MetricsResult.MergeResponseDays(responseParts),
                FirstResponseHistogram = histogram,
                FirstResponseSamples = samples,
            }
            : Empty;
    }

    // `typeNames` resolve o nome de exibição de cada tipo de desfecho.
    public MetricsDto ToDto(IReadOnlyDictionary<string, string> typeNames)
    {
        var unanswered = ConversationsStarted - ConversationsAnswered;
        // Amostras cruas presentes (período curto, calculado ao vivo) → mediana exata;
        // caso contrário, estimada pelo histograma.
        var median = FirstResponseSamples.Count > 0
            ? MetricsResult.Median(FirstResponseSamples)
            : FirstResponseBuckets.EstimateMedian(FirstResponseHistogram);

        // Espera de resposta: cada DIA pesa igual, independentemente de quantas
        // respostas teve. Um dia com uma resposta lenta não é diluído por um dia
        // de muitas respostas rápidas.
        var avgResponse = ResponseByDay.Count == 0
            ? (double?)null
            : ResponseByDay.Values.Average(d => d.AvgMinutes);

        // Denominador é o tempo COBERTO, não a duração da janela: dois números só
        // dão 100% se os dois ficaram no ar o período inteiro, e sem cobertura
        // (vendedor sem número, número que já não é dele) o valor é null → "—".
        var uptime = CoveredSeconds <= 0
            ? (double?)null
            : Math.Clamp((CoveredSeconds - DowntimeSeconds) / CoveredSeconds * 100, 0, 100);

        var sale = Outcomes.GetValueOrDefault(OutcomeTypeCodes.Sale, OutcomeTotals.Empty);

        // Todo tipo conhecido aparece (mesmo zerado), para o painel não "perder"
        // um card quando não houve desfecho daquele tipo no período.
        var outcomeDtos = typeNames
            .Select(kv =>
            {
                var totals = Outcomes.GetValueOrDefault(kv.Key, OutcomeTotals.Empty);
                return new OutcomeMetricDto(
                    kv.Key,
                    kv.Value,
                    totals.Count,
                    ConversationsAnswered == 0 ? null : (double)totals.Count / ConversationsAnswered,
                    totals.AvgTimeToCloseHours);
            })
            .ToList();

        return new MetricsDto(
            ConversationsStarted,
            ConversationsAnswered,
            unanswered,
            OutboundConversationsStarted,
            OutboundConversationsEngaged,
            ConversationsStarted == 0 ? null : (double)ConversationsAnswered / ConversationsStarted,
            median,
            avgResponse,
            ResponseByDay.Count == 0 ? null : ResponseByDay.Values.Min(d => d.MinMinutes),
            ResponseByDay.Count == 0 ? null : ResponseByDay.Values.Max(d => d.MaxMinutes),
            ResponseByDay.Values.Sum(d => d.Count),
            [.. ResponseByDay
                .OrderBy(kv => kv.Key)
                .Select(kv => new ResponseWaitDayDto(
                    kv.Key, kv.Value.Count, kv.Value.SumMinutes, kv.Value.MinMinutes, kv.Value.MaxMinutes))],
            MessagesSent,
            MessagesReceived,
            MessagesReceived == 0 ? null : (double)MessagesSent / MessagesReceived,
            MessagesSent == 0 ? null : (double)OutboundRead / MessagesSent,
            SilenceGaps == 0 ? null : (double)SilenceGapsFollowedUp / SilenceGaps,
            sale.Count,
            ConversationsAnswered == 0 ? null : (double)sale.Count / ConversationsAnswered,
            sale.AvgTimeToCloseHours,
            EffectiveBusinessHours > 0 ? MessagesSent / EffectiveBusinessHours : null,
            EffectiveBusinessHours > 0 ? MessagesReceived / EffectiveBusinessHours : null,
            EffectiveBusinessHours,
            LastOutboundMessageAt,
            uptime,
            CoveredSeconds,
            DowntimeSeconds,
            BanCount,
            outcomeDtos);
    }

    private static double? Smaller(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static double? Larger(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static DateTime? Later(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
