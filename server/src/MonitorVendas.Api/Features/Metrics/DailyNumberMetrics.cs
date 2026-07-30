namespace MonitorVendas.Api.Features.Metrics;

// Resumo fechado de UM dia (dia local do fuso de Metrics:TimeZone) de UM número.
// O relatório soma essas linhas em vez de reprocessar mensagens.
//
// Só entram grandezas que somam entre dias. A mediana da 1ª resposta não soma:
// guardamos o histograma (faixas) e a estimamos por interpolação; períodos curtos
// continuam sendo calculados ao vivo (mediana exata).
public class DailyNumberMetrics
{
    public Guid WhatsappNumberId { get; set; }
    public DateOnly Day { get; set; }

    public int ConversationsStarted { get; set; }
    public int ConversationsAnswered { get; set; }
    public int OutboundConversationsStarted { get; set; }
    public int OutboundConversationsEngaged { get; set; }
    public int MessagesSent { get; set; }
    public int MessagesReceived { get; set; }
    public int OutboundRead { get; set; }
    public int SilenceGaps { get; set; }
    public int SilenceGapsFollowedUp { get; set; }
    public int BanCount { get; set; }

    public int ResponseCount { get; set; }
    public double ResponseMinutesSum { get; set; }
    public double? ResponseMinutesMin { get; set; }
    public double? ResponseMinutesMax { get; set; }

    public double EffectiveBusinessHours { get; set; }
    public double DowntimeSeconds { get; set; }
    public DateTime? LastOutboundMessageAt { get; set; }

    // Histograma da 1ª resposta (minutos úteis) nas faixas de FirstResponseBuckets.
    public int[] FirstResponseHistogram { get; set; } = new int[FirstResponseBuckets.Count];

    public DateTime ComputedAt { get; set; }
}

// Faixas estreitas onde importa (respostas rápidas) e largas na cauda.
public static class FirstResponseBuckets
{
    // Limite superior de cada faixa, em minutos; a última é aberta (8h+).
    public static readonly double[] UpperBounds = [1, 2, 5, 10, 20, 30, 60, 120, 240, 480, double.PositiveInfinity];

    public static int Count => UpperBounds.Length;

    public static int IndexOf(double minutes)
    {
        for (var i = 0; i < UpperBounds.Length; i++)
            if (minutes <= UpperBounds[i])
                return i;

        return UpperBounds.Length - 1;
    }

    public static double LowerBound(int index) => index == 0 ? 0 : UpperBounds[index - 1];

    // Mediana estimada: encontra a faixa onde cai o elemento do meio e interpola
    // linearmente dentro dela. Na última faixa (aberta) devolve o piso.
    public static double? EstimateMedian(IReadOnlyList<int> histogram)
    {
        var total = histogram.Sum();
        if (total == 0)
            return null;

        var target = total / 2.0;
        var cumulative = 0;

        for (var i = 0; i < histogram.Count; i++)
        {
            if (histogram[i] == 0)
                continue;

            if (cumulative + histogram[i] >= target)
            {
                var lower = LowerBound(i);
                var upper = UpperBounds[i];
                if (double.IsPositiveInfinity(upper))
                    return lower;

                var positionInBucket = (target - cumulative) / histogram[i];
                return lower + (upper - lower) * positionInBucket;
            }

            cumulative += histogram[i];
        }

        return LowerBound(histogram.Count - 1);
    }
}

// Desfechos do dia por TIPO (venda, cliente perdido, ...). Tabela filha para que
// criar um tipo novo não exija migração nem coluna nova.
public class DailyNumberOutcomeMetrics
{
    public Guid WhatsappNumberId { get; set; }
    public DateOnly Day { get; set; }
    public string OutcomeTypeCode { get; set; } = string.Empty;

    public int Count { get; set; }
    public int TimeToCloseCount { get; set; }
    public double TimeToCloseHoursSum { get; set; }
}

// Dia que precisa ser (re)calculado. Marcado pelo pipeline de webhooks e pelo
// rebuild; consumido pelo serviço de agregação.
public class DirtyMetricsDay
{
    public Guid WhatsappNumberId { get; set; }
    public DateOnly Day { get; set; }
    public DateTime MarkedAt { get; set; }
}
