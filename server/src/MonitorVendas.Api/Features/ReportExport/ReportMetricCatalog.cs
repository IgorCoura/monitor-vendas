using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

public sealed record ReportMetric(string Key, string Label, Func<MetricsDto, double?> Value, string Format)
{
    public const string Count = "0";
    public const string Decimal = "0.0";
    public const string Percent = "0.0%";
    public const string Ratio = "0.00";
}

// Espelha as métricas do painel. Os tipos de desfecho entram de forma dinâmica:
// tipo novo criado na tela vira coluna e opção de gráfico sem código aqui.
public static class ReportMetricCatalog
{
    public static IReadOnlyList<ReportMetric> Build(IReadOnlyList<OutcomeMetricDto> outcomes)
    {
        var metrics = new List<ReportMetric>
        {
            new("conversationsStarted", "Conversas iniciadas", m => m.ConversationsStarted, ReportMetric.Count),
            new("conversationsAnswered", "Conversas atendidas", m => m.ConversationsAnswered, ReportMetric.Count),
            new("conversationsUnanswered", "Não respondidas", m => m.ConversationsUnanswered, ReportMetric.Count),
            new("outboundConversationsStarted", "Disparos", m => m.OutboundConversationsStarted, ReportMetric.Count),
            new("outboundConversationsEngaged", "Captações", m => m.OutboundConversationsEngaged, ReportMetric.Count),
            new("responseRate", "Taxa de resposta", m => m.ResponseRate, ReportMetric.Percent),
            new("medianFirstResponseMinutes", "Mediana da 1ª resposta (min)", m => m.MedianFirstResponseMinutes, ReportMetric.Decimal),
            new("avgResponseMinutes", "Espera média (min)", m => m.AvgResponseMinutes, ReportMetric.Decimal),
            new("minResponseMinutes", "Espera mínima (min)", m => m.MinResponseMinutes, ReportMetric.Decimal),
            new("maxResponseMinutes", "Espera máxima (min)", m => m.MaxResponseMinutes, ReportMetric.Decimal),
            new("messagesSent", "Mensagens enviadas", m => m.MessagesSent, ReportMetric.Count),
            new("messagesReceived", "Mensagens recebidas", m => m.MessagesReceived, ReportMetric.Count),
            new("sentReceivedRatio", "Razão enviadas/recebidas", m => m.SentReceivedRatio, ReportMetric.Ratio),
            new("avgSentPerBusinessHour", "Média enviadas por hora útil", m => m.AvgSentPerBusinessHour, ReportMetric.Ratio),
            new("avgReceivedPerBusinessHour", "Média recebidas por hora útil", m => m.AvgReceivedPerBusinessHour, ReportMetric.Ratio),
            new("readRate", "Taxa de leitura", m => m.ReadRate, ReportMetric.Percent),
            new("followUpRate", "Follow-up", m => m.FollowUpRate, ReportMetric.Percent),
            new("sales", "Vendas", m => m.Sales, ReportMetric.Count),
            new("conversionRate", "Conversão", m => m.ConversionRate, ReportMetric.Percent),
            new("avgTimeToCloseBusinessHours", "Tempo até fechar (h úteis)", m => m.AvgTimeToCloseBusinessHours, ReportMetric.Decimal),
            new("effectiveBusinessHours", "Horas úteis efetivas", m => m.EffectiveBusinessHours, ReportMetric.Decimal),
            new("uptimePercent", "Uptime", m => m.UptimePercent / 100, ReportMetric.Percent),
            new("banCount", "Bans", m => m.BanCount, ReportMetric.Count),
        };

        foreach (var outcome in outcomes.Where(o => o.TypeCode != Outcomes.OutcomeTypeCodes.Sale))
        {
            var code = outcome.TypeCode;
            metrics.Add(new($"outcome:{code}", outcome.Name, m => CountOf(m, code), ReportMetric.Count));
        }

        return metrics;
    }

    private static double? CountOf(MetricsDto metrics, string code) =>
        metrics.Outcomes.FirstOrDefault(o => o.TypeCode == code)?.Count;

    // Vazio significa "todas": filtro não informado não pode devolver planilha vazia.
    public static IReadOnlyList<ReportMetric> Select(IReadOnlyList<ReportMetric> catalog, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return catalog;

        var wanted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. catalog.Where(m => wanted.Contains(m.Key))];
    }
}
