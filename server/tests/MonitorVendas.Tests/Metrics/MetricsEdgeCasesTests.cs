using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Tests.Metrics;

// As bordas do cálculo: divisão por zero vira null (nunca 0, que seria um número
// errado com cara de certo) e intervalos de downtime que se sobrepõem não podem
// ser contados duas vezes.
public class MetricsEdgeCasesTests
{
    private static readonly DateTime From = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(4);

    private static MetricsCalculator Calculator() =>
        new(new BusinessHoursCalendar(TimeZoneInfo.Utc, 0, 24, true, 0, 24, new HashSet<DateOnly>()), new MetricsOptions());

    private static MetricsResult Empty() =>
        Calculator().Compute(From, To, [], [], 0);

    // Período sem nada: toda taxa é desconhecida, não zero. Zero diria "ninguém
    // respondeu"; null diz "não houve o que responder".
    [Fact]
    public void Compute_WithNoData_ReturnsNullRatesInsteadOfZero()
    {
        var result = Empty();

        Assert.Null(result.ResponseRate);
        Assert.Null(result.ConversionRate);
        Assert.Null(result.ReadRate);
        Assert.Null(result.FollowUpRate);
        Assert.Null(result.SentReceivedRatio);
        Assert.Null(result.MedianFirstResponseMinutes);
        Assert.Null(result.AvgResponseMinutes);
        Assert.Null(result.MinResponseMinutes);
        Assert.Null(result.MaxResponseMinutes);
        Assert.Null(result.LastOutboundMessageAt);
        Assert.Equal(0, result.ConversationsUnanswered);
    }

    // Sem hora útil efetiva (número fora do ar o período inteiro), a média por
    // hora não existe — dividir por zero daria infinito. Com hora útil e nenhuma
    // mensagem, aí sim é zero de verdade.
    [Fact]
    public void AveragePerBusinessHour_WithoutBusinessTime_IsNull()
    {
        var semRelogio = Calculator().Compute(From, To, [], [new DowntimeInterval(From, To)], 0);
        Assert.Null(semRelogio.AvgSentPerBusinessHour);
        Assert.Null(semRelogio.AvgReceivedPerBusinessHour);

        var comRelogio = Empty();
        Assert.Equal(0, comRelogio.AvgSentPerBusinessHour);
        Assert.Equal(0, comRelogio.AvgReceivedPerBusinessHour);
    }

    // Mediana com número par de amostras é a média das duas do meio; com ímpar, a
    // do meio. Lista vazia não tem mediana.
    [Fact]
    public void Median_HandlesEvenOddAndEmpty()
    {
        Assert.Equal(3, MetricsResult.Median([5, 1, 3]));
        Assert.Equal(4, MetricsResult.Median([5, 1, 3, 7]));
        Assert.Equal(2, MetricsResult.Median([2]));
        Assert.Null(MetricsResult.Median([]));
    }

    // Agregar lista vazia devolve o resultado neutro — uptime 100%, porque não
    // houve período nenhum em que o número esteve fora.
    [Fact]
    public void Aggregate_WithNoParts_IsNeutral()
    {
        var result = MetricsResult.Aggregate([]);

        Assert.Equal(0, result.ConversationsStarted);
        Assert.Equal(100, result.UptimePercent);
        Assert.Empty(result.FirstResponseMinutesSamples);
    }

    // Período inteiro fora do ar: uptime zero, e o relógio útil não conta nada
    // contra o vendedor.
    [Fact]
    public void Compute_WithFullDowntime_HasZeroUptime()
    {
        var result = Calculator().Compute(From, To, [], [new DowntimeInterval(From, To)], 0);

        Assert.Equal(0, result.UptimePercent);
        Assert.Equal(0, result.EffectiveBusinessHours);
    }

    // Downtimes sobrepostos são fundidos: contar duas vezes o mesmo intervalo
    // levaria a uptime negativo.
    [Fact]
    public void Compute_WithOverlappingDowntimes_MergesThem()
    {
        var downtimes = new[]
        {
            new DowntimeInterval(From, From.AddHours(2)),
            new DowntimeInterval(From.AddHours(1), From.AddHours(3)),
        };

        var result = Calculator().Compute(From, To, [], downtimes, 0);

        // Três horas fora de quatro: 25% de uptime, não menos.
        Assert.Equal(25, result.UptimePercent, 1);
        Assert.Equal(1, result.EffectiveBusinessHours, 1);
    }

    // Downtime que começa antes e termina depois do período é recortado à janela.
    [Fact]
    public void Compute_ClipsDowntimeToThePeriod()
    {
        var result = Calculator().Compute(
            From, To, [], [new DowntimeInterval(From.AddDays(-1), To.AddDays(1))], 0);

        Assert.Equal(0, result.UptimePercent);
    }

    // Downtime inteiramente fora do período não afeta nada.
    [Fact]
    public void Compute_IgnoresDowntimeOutsideThePeriod()
    {
        var result = Calculator().Compute(
            From, To, [], [new DowntimeInterval(From.AddDays(-2), From.AddDays(-1))], 0);

        Assert.Equal(100, result.UptimePercent);
        Assert.Equal(4, result.EffectiveBusinessHours, 1);
    }

    // Período invertido ou nulo não quebra o cálculo — uptime 100% por definição.
    [Fact]
    public void Compute_WithEmptyPeriod_DoesNotDivideByZero()
    {
        var result = Calculator().Compute(From, From, [], [], 0);

        Assert.Equal(100, result.UptimePercent);
    }
}
