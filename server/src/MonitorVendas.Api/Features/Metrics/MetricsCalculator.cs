using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Api.Features.Metrics;

public sealed record MessageData(DateTime Timestamp, bool IsInbound, DateTime? ReadAt);

public sealed record ConversationData(
    DateTime StartedAt,
    bool StartedByContact,
    IReadOnlyList<MessageData> Messages,
    DateTime? OutcomeMarkedAt = null,
    string? OutcomeTypeCode = null);

// Espera de resposta consolidada de UM dia. A amostra pertence ao dia da
// RESPOSTA — é esse o dia que o pipeline marca como sujo, então resposta que
// demora dias não some do agregado (atribuindo à pergunta, sumia).
//
// O período combina essas consolidações, nunca as amostras cruas: o mínimo é o
// menor dos mínimos do dia, o máximo o maior dos máximos, e a média é a média
// das médias diárias — cada dia pesa igual, independentemente do volume.
public sealed record ResponseDayStats(int Count, double SumMinutes, double MinMinutes, double MaxMinutes)
{
    public double AvgMinutes => Count == 0 ? 0 : SumMinutes / Count;

    public static ResponseDayStats Of(double minutes) => new(1, minutes, minutes, minutes);

    public ResponseDayStats Plus(ResponseDayStats other) => new(
        Count + other.Count,
        SumMinutes + other.SumMinutes,
        Math.Min(MinMinutes, other.MinMinutes),
        Math.Max(MaxMinutes, other.MaxMinutes));
}

// Totais de um tipo de desfecho (venda, cliente perdido, ...). Somável entre
// números e entre dias.
public sealed record OutcomeTotals(int Count, int TimeToCloseCount, double TimeToCloseHoursSum)
{
    public static OutcomeTotals Empty => new(0, 0, 0);

    public double? AvgTimeToCloseHours => TimeToCloseCount == 0 ? null : TimeToCloseHoursSum / TimeToCloseCount;

    public OutcomeTotals Plus(OutcomeTotals other) => new(
        Count + other.Count,
        TimeToCloseCount + other.TimeToCloseCount,
        TimeToCloseHoursSum + other.TimeToCloseHoursSum);
}

// Resultado por número; os *Samples ficam expostos para agregação por vendedor
// (mediana não se combina a partir de medianas parciais). EffectiveBusinessHours
// permite reagregar as médias por hora útil (soma de mensagens ÷ soma de horas).
//
// Uptime sai de DOIS somáveis — tempo coberto e tempo fora do ar — em vez de um
// percentual pronto. Percentual não soma entre números nem entre dias, e era daí
// que vinha o vendedor de canal derrubado aparecendo com 100%.
public sealed record MetricsResult(
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
    double CoveredSeconds,
    double DowntimeSeconds,
    double EffectiveBusinessHours,
    DateTime? LastOutboundMessageAt,
    IReadOnlyList<double> FirstResponseMinutesSamples,
    // Amostras cruas na ordem em que aconteceram — diagnóstico e testes. Quem manda
    // no número exibido é `ResponseByDay`, a consolidação por dia.
    IReadOnlyList<double> ResponseMinutesSamples,
    IReadOnlyDictionary<DateOnly, ResponseDayStats> ResponseByDay)
{
    public int ConversationsUnanswered => ConversationsStarted - ConversationsAnswered;
    public double? ResponseRate => ConversationsStarted == 0 ? null : (double)ConversationsAnswered / ConversationsStarted;
    public double? MedianFirstResponseMinutes => Median(FirstResponseMinutesSamples);
    public int ResponseSamplesCount => ResponseByDay.Values.Sum(d => d.Count);
    public double? AvgResponseMinutes => ResponseByDay.Count == 0 ? null : ResponseByDay.Values.Average(d => d.AvgMinutes);
    public double? MinResponseMinutes => ResponseByDay.Count == 0 ? null : ResponseByDay.Values.Min(d => d.MinMinutes);
    public double? MaxResponseMinutes => ResponseByDay.Count == 0 ? null : ResponseByDay.Values.Max(d => d.MaxMinutes);
    public double? SentReceivedRatio => MessagesReceived == 0 ? null : (double)MessagesSent / MessagesReceived;
    public double? ReadRate => MessagesSent == 0 ? null : (double)OutboundRead / MessagesSent;
    public double? FollowUpRate => SilenceGaps == 0 ? null : (double)SilenceGapsFollowedUp / SilenceGaps;
    public double? AvgSentPerBusinessHour => EffectiveBusinessHours > 0 ? MessagesSent / EffectiveBusinessHours : null;
    public double? AvgReceivedPerBusinessHour => EffectiveBusinessHours > 0 ? MessagesReceived / EffectiveBusinessHours : null;

    // Sem tempo coberto não há uptime a informar — devolve null ("—"), nunca 100%.
    // Vendedor sem número no ar não tem canal perfeito: não tem canal.
    public double? UptimePercent => CoveredSeconds <= 0
        ? null
        : Math.Clamp((CoveredSeconds - DowntimeSeconds) / CoveredSeconds * 100, 0, 100);

    // Venda continua em destaque (compatibilidade do painel); os demais tipos
    // saem de Outcomes.
    public int Sales => Outcomes.GetValueOrDefault(OutcomeTypeCodes.Sale, OutcomeTotals.Empty).Count;
    public double? ConversionRate => ConversationsAnswered == 0 ? null : (double)Sales / ConversationsAnswered;
    public double? AvgTimeToCloseBusinessHours =>
        Outcomes.GetValueOrDefault(OutcomeTypeCodes.Sale, OutcomeTotals.Empty).AvgTimeToCloseHours;

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static MetricsResult Aggregate(IReadOnlyList<MetricsResult> parts) => parts.Count == 0
        ? new(0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, OutcomeTotals>(), 0, 0, 0, 0, null, [], [],
            new Dictionary<DateOnly, ResponseDayStats>())
        : new(
            parts.Sum(p => p.ConversationsStarted),
            parts.Sum(p => p.ConversationsAnswered),
            parts.Sum(p => p.OutboundConversationsStarted),
            parts.Sum(p => p.OutboundConversationsEngaged),
            parts.Sum(p => p.MessagesSent),
            parts.Sum(p => p.MessagesReceived),
            parts.Sum(p => p.OutboundRead),
            parts.Sum(p => p.SilenceGaps),
            parts.Sum(p => p.SilenceGapsFollowedUp),
            MergeOutcomes(parts.Select(p => p.Outcomes)),
            parts.Sum(p => p.BanCount),
            parts.Sum(p => p.CoveredSeconds),
            parts.Sum(p => p.DowntimeSeconds),
            parts.Sum(p => p.EffectiveBusinessHours),
            parts.Max(p => p.LastOutboundMessageAt),
            [.. parts.SelectMany(p => p.FirstResponseMinutesSamples)],
            [.. parts.SelectMany(p => p.ResponseMinutesSamples)],
            MergeResponseDays(parts.Select(p => p.ResponseByDay)));

    // Combina POR DIA: dois números do mesmo vendedor no mesmo dia viram um dia só,
    // senão o dia entraria duas vezes na média das médias.
    public static Dictionary<DateOnly, ResponseDayStats> MergeResponseDays(
        IEnumerable<IReadOnlyDictionary<DateOnly, ResponseDayStats>> parts)
    {
        var merged = new Dictionary<DateOnly, ResponseDayStats>();

        foreach (var part in parts)
        {
            foreach (var (day, stats) in part)
                merged[day] = merged.TryGetValue(day, out var current) ? current.Plus(stats) : stats;
        }

        return merged;
    }

    public static Dictionary<string, OutcomeTotals> MergeOutcomes(IEnumerable<IReadOnlyDictionary<string, OutcomeTotals>> parts)
    {
        var merged = new Dictionary<string, OutcomeTotals>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            foreach (var (code, totals) in part)
                merged[code] = merged.GetValueOrDefault(code, OutcomeTotals.Empty).Plus(totals);
        }

        return merged;
    }
}

public sealed class MetricsCalculator(BusinessHoursCalendar calendar, MetricsOptions options)
{
    // `coveredSeconds` é o denominador do uptime: quanto tempo da janela este canal
    // realmente respondia por este vendedor (o número pode ter nascido no meio do
    // período, ou ter sido de outra pessoa). Nulo = a janela inteira.
    public MetricsResult Compute(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<ConversationData> conversations,
        IReadOnlyList<DowntimeInterval> downtimes,
        int banCount,
        double? coveredSeconds = null)
    {
        var merged = MergeAndClip(downtimes, fromUtc, toUtc);
        var answerWindow = TimeSpan.FromHours(options.AnswerWindowBusinessHours);
        var followUpGap = TimeSpan.FromHours(options.FollowUpGapBusinessHours);

        int started = 0, answered = 0, sent = 0, received = 0, outboundRead = 0;
        int outboundStarted = 0, outboundEngaged = 0;
        int withGap = 0, followedUp = 0;
        DateTime? lastOutbound = null;
        var firstResponseSamples = new List<double>();
        var responseSamples = new List<double>();
        var responseByDay = new Dictionary<DateOnly, ResponseDayStats>();
        var outcomes = new Dictionary<string, OutcomeTotals>(StringComparer.Ordinal);

        foreach (var conversation in conversations)
        {
            var ordered = conversation.Messages.OrderBy(m => m.Timestamp).ToList();

            // Uma única varredura de trás para frente resolve "qual é a próxima
            // resposta do vendedor" para toda a conversa (antes era uma busca por
            // mensagem, O(n²) por conversa).
            var nextOutbound = new DateTime?[ordered.Count];
            DateTime? seenOutbound = null;
            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                nextOutbound[i] = ordered[i].IsInbound ? seenOutbound : ordered[i].Timestamp;
                if (!ordered[i].IsInbound)
                    seenOutbound = ordered[i].Timestamp;
            }

            // Espera de resposta: cada mensagem do vendedor fecha a espera aberta
            // pela PRIMEIRA mensagem do cliente ainda não respondida — é desde ela
            // que o cliente está esperando, não desde a última que ele mandou.
            // Mensagem seguinte do vendedor não tem espera para fechar, e só volta
            // a contar depois que o cliente escrever de novo; disparo (conversa
            // aberta pelo vendedor) também não fecha nada, porque não há ninguém
            // esperando. O ponteiro atravessa mensagens fora da janela: quem manda
            // a amostra para o relatório é o dia da RESPOSTA, não o da pergunta.
            DateTime? waitingSince = null;

            for (var i = 0; i < ordered.Count; i++)
            {
                var message = ordered[i];
                var inRange = InRange(message.Timestamp, fromUtc, toUtc);

                if (message.IsInbound)
                {
                    if (inRange)
                        received++;

                    waitingSince ??= message.Timestamp;
                    continue;
                }

                if (inRange)
                {
                    sent++;
                    if (message.ReadAt is not null)
                        outboundRead++;
                    if (lastOutbound is null || message.Timestamp > lastOutbound)
                        lastOutbound = message.Timestamp;

                    if (waitingSince is { } since)
                    {
                        var minutes = calendar.BusinessTimeBetween(since, message.Timestamp, merged).TotalMinutes;
                        var day = calendar.LocalDayOf(message.Timestamp);

                        responseSamples.Add(minutes);
                        responseByDay[day] = responseByDay.TryGetValue(day, out var stats)
                            ? stats.Plus(ResponseDayStats.Of(minutes))
                            : ResponseDayStats.Of(minutes);
                    }
                }

                waitingSince = null;
            }

            if (conversation.StartedByContact && InRange(conversation.StartedAt, fromUtc, toUtc))
            {
                started++;
                var firstInboundIndex = ordered.FindIndex(m => m.IsInbound);
                var firstReply = firstInboundIndex >= 0 ? nextOutbound[firstInboundIndex] : null;

                if (firstInboundIndex >= 0 && firstReply is { } reply)
                {
                    var elapsed = calendar.BusinessTimeBetween(ordered[firstInboundIndex].Timestamp, reply, merged);
                    firstResponseSamples.Add(elapsed.TotalMinutes);
                    if (elapsed <= answerWindow)
                        answered++;
                }
            }

            // Disparo: conversa iniciada pelo vendedor; Captação: disparo que obteve
            // qualquer resposta do cliente.
            if (!conversation.StartedByContact && InRange(conversation.StartedAt, fromUtc, toUtc))
            {
                outboundStarted++;
                if (ordered.Any(m => m.IsInbound))
                    outboundEngaged++;
            }

            // Follow-up: conta CADA silêncio >= limite (não a conversa) — a mesma
            // conversa pode esfriar e ser resgatada várias vezes, e cada silêncio
            // tem data própria, o que permite fechar o número por dia.
            // Silêncio que nunca foi quebrado não entra (não há mensagem depois).
            for (var i = 1; i < ordered.Count; i++)
            {
                if (!InRange(ordered[i].Timestamp, fromUtc, toUtc))
                    continue;

                var gap = calendar.BusinessTimeBetween(ordered[i - 1].Timestamp, ordered[i].Timestamp, merged);
                if (gap < followUpGap)
                    continue;

                withGap++;
                if (!ordered[i].IsInbound)
                    followedUp++;
            }

            // Desfecho (venda, cliente perdido, ...) é atribuído ao dia da marcação.
            if (conversation.OutcomeMarkedAt is { } markedAt
                && conversation.OutcomeTypeCode is { } typeCode
                && InRange(markedAt, fromUtc, toUtc))
            {
                var hours = calendar.BusinessTimeBetween(conversation.StartedAt, markedAt, merged).TotalHours;
                var current = outcomes.GetValueOrDefault(typeCode, OutcomeTotals.Empty);
                outcomes[typeCode] = current.Plus(new OutcomeTotals(1, 1, hours));
            }
        }

        return new MetricsResult(
            started, answered, outboundStarted, outboundEngaged, sent, received, outboundRead,
            withGap, followedUp, outcomes, banCount,
            coveredSeconds ?? Math.Max(0, (toUtc - fromUtc).TotalSeconds),
            DowntimeSecondsOf(merged),
            calendar.BusinessTimeBetween(fromUtc, toUtc, merged).TotalHours,
            lastOutbound,
            firstResponseSamples, responseSamples, responseByDay);
    }

    private static bool InRange(DateTime value, DateTime fromUtc, DateTime toUtc) =>
        value >= fromUtc && value < toUtc;

    // Os intervalos já chegam recortados na janela e sem sobreposição.
    private static double DowntimeSecondsOf(IReadOnlyList<DowntimeInterval> merged)
    {
        var total = TimeSpan.Zero;
        foreach (var interval in merged)
            total += interval.End - interval.Start;

        return total.TotalSeconds;
    }

    private static List<DowntimeInterval> MergeAndClip(IReadOnlyList<DowntimeInterval> downtimes, DateTime fromUtc, DateTime toUtc)
    {
        var clipped = downtimes
            .Select(d => new DowntimeInterval(d.Start > fromUtc ? d.Start : fromUtc, d.End < toUtc ? d.End : toUtc))
            .Where(d => d.End > d.Start)
            .OrderBy(d => d.Start)
            .ToList();

        var merged = new List<DowntimeInterval>();
        foreach (var interval in clipped)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = interval.End > merged[^1].End ? interval.End : merged[^1].End };
            else
                merged.Add(interval);
        }

        return merged;
    }
}
