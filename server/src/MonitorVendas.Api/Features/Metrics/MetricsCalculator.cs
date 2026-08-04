using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Api.Features.Metrics;

public sealed record MessageData(DateTime Timestamp, bool IsInbound, DateTime? ReadAt);

public sealed record ConversationData(
    DateTime StartedAt,
    bool StartedByContact,
    IReadOnlyList<MessageData> Messages,
    DateTime? OutcomeMarkedAt = null,
    string? OutcomeTypeCode = null);

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
    IReadOnlyList<double> ResponseMinutesSamples)
{
    public int ConversationsUnanswered => ConversationsStarted - ConversationsAnswered;
    public double? ResponseRate => ConversationsStarted == 0 ? null : (double)ConversationsAnswered / ConversationsStarted;
    public double? MedianFirstResponseMinutes => Median(FirstResponseMinutesSamples);
    public double? AvgResponseMinutes => ResponseMinutesSamples.Count == 0 ? null : ResponseMinutesSamples.Average();
    public double? MinResponseMinutes => ResponseMinutesSamples.Count == 0 ? null : ResponseMinutesSamples.Min();
    public double? MaxResponseMinutes => ResponseMinutesSamples.Count == 0 ? null : ResponseMinutesSamples.Max();
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
        ? new(0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, OutcomeTotals>(), 0, 0, 0, 0, null, [], [])
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
            [.. parts.SelectMany(p => p.ResponseMinutesSamples)]);

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

            for (var i = 0; i < ordered.Count; i++)
            {
                var message = ordered[i];
                if (!InRange(message.Timestamp, fromUtc, toUtc))
                    continue;

                if (message.IsInbound)
                {
                    received++;
                    // Espera do cliente: qualquer mensagem dele que teve resposta,
                    // inclusive quando a resposta cai depois do fim do período.
                    if (nextOutbound[i] is { } reply)
                        responseSamples.Add(calendar.BusinessTimeBetween(message.Timestamp, reply, merged).TotalMinutes);
                }
                else
                {
                    sent++;
                    if (message.ReadAt is not null)
                        outboundRead++;
                    if (lastOutbound is null || message.Timestamp > lastOutbound)
                        lastOutbound = message.Timestamp;
                }
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
            firstResponseSamples, responseSamples);
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
