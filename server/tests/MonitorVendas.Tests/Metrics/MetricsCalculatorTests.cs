using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Tests.Metrics;

public class MetricsCalculatorTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static MetricsCalculator NewCalculator() =>
        new(new BusinessHoursCalendar(SaoPaulo), new MetricsOptions());

    private static DateTime Local(int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 7, day, hour, minute, 0, DateTimeKind.Unspecified), SaoPaulo);

    private static readonly DateTime From = Local(1, 0);
    private static readonly DateTime To = Local(20, 0);

    private static MessageData In(DateTime ts) => new(ts, IsInbound: true, ReadAt: null);
    private static MessageData Out(DateTime ts, bool read = false) => new(ts, IsInbound: false, read ? ts.AddMinutes(5) : null);

    // Conversa respondida em 30min útil conta como atendida; sem resposta não conta — taxa 0,5.
    [Fact]
    public void ResponseRate_CountsOnlyAnsweredConversations()
    {
        var answered = new ConversationData(Local(1, 10), true, [In(Local(1, 10)), Out(Local(1, 10, 30))], null);
        var ignored = new ConversationData(Local(2, 11), true, [In(Local(2, 11))], null);

        var result = NewCalculator().Compute(From, To, [answered, ignored], [], 0);

        Assert.Equal(2, result.ConversationsStarted);
        Assert.Equal(1, result.ConversationsAnswered);
        Assert.Equal(0.5, result.ResponseRate);
        Assert.Equal(30, result.MedianFirstResponseMinutes);
    }

    // Resposta que só veio após mais de 24h ÚTEIS não conta como atendimento (mas registra o tempo).
    [Fact]
    public void ReplyBeyondAnswerWindow_NotCountedAsAnswered()
    {
        // dia 1 10h → dia 4 10h = 8h + 9h + 9h + 1h = 27h úteis.
        var conv = new ConversationData(Local(1, 10), true, [In(Local(1, 10)), Out(Local(4, 10))], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(0, result.ConversationsAnswered);
        Assert.Equal(27 * 60, result.MedianFirstResponseMinutes);
    }

    // Mensagem recebida às 17h50 respondida 9h10 do dia seguinte = 20min úteis, não 15h de relógio.
    [Fact]
    public void OvernightReply_UsesBusinessClock()
    {
        var conv = new ConversationData(Local(1, 17, 50), true, [In(Local(1, 17, 50)), Out(Local(2, 9, 10))], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(20, result.MedianFirstResponseMinutes);
    }

    // Downtime (ban) entre a pergunta e a resposta é descontado: o vendedor não é punido pelo canal fora do ar.
    [Fact]
    public void Downtime_ExcludedFromResponseClock()
    {
        var conv = new ConversationData(Local(1, 10), true, [In(Local(1, 10)), Out(Local(1, 17))], null);
        var downtimes = new[] { new DowntimeInterval(Local(1, 10, 30), Local(1, 16, 30)) };

        var result = NewCalculator().Compute(From, To, [conv], downtimes, 1);

        Assert.Equal(60, result.MedianFirstResponseMinutes);
        Assert.Equal(1, result.BanCount);
    }

    // Gap de silêncio >24h úteis fechado por mensagem do vendedor conta follow-up; fechado pelo cliente, não.
    [Fact]
    public void FollowUpRate_OnlySellerReengagementCounts()
    {
        var followed = new ConversationData(Local(1, 10), true,
            [In(Local(1, 10)), Out(Local(1, 10, 5)), Out(Local(8, 10))], null);
        var notFollowed = new ConversationData(Local(1, 11), true,
            [In(Local(1, 11)), Out(Local(1, 11, 5)), In(Local(8, 11))], null);

        var result = NewCalculator().Compute(From, To, [followed, notFollowed], [], 0);

        Assert.Equal(2, result.SilenceGaps);
        Assert.Equal(1, result.SilenceGapsFollowedUp);
        Assert.Equal(0.5, result.FollowUpRate);
    }

    // Cada silêncio conta separadamente: a mesma conversa esfriando duas vezes e
    // sendo resgatada duas vezes vale 2 (necessário para fechar o número por dia).
    [Fact]
    public void FollowUp_CountsEachSilenceGap_NotConversations()
    {
        var conv = new ConversationData(Local(1, 10), true,
            [In(Local(1, 10)), Out(Local(1, 10, 5)), Out(Local(8, 10)), Out(Local(15, 10))], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(2, result.SilenceGaps);
        Assert.Equal(2, result.SilenceGapsFollowedUp);
    }

    // Venda marcada conta em Sales, conversão sobre atendidas e tempo até fechar em horas úteis.
    [Fact]
    public void Sales_ConversionAndTimeToClose()
    {
        var sold = new ConversationData(Local(1, 10), true,
            [In(Local(1, 10)), Out(Local(1, 10, 10))], OutcomeMarkedAt: Local(1, 15), OutcomeTypeCode: OutcomeTypeCodes.Sale);
        var open = new ConversationData(Local(2, 10), true,
            [In(Local(2, 10)), Out(Local(2, 10, 10))], null);

        var result = NewCalculator().Compute(From, To, [sold, open], [], 0);

        Assert.Equal(1, result.Sales);
        Assert.Equal(0.5, result.ConversionRate);
        Assert.Equal(5, result.AvgTimeToCloseBusinessHours);
    }

    // Contagens de enviadas/recebidas e taxa de leitura das enviadas.
    [Fact]
    public void MessageCounts_AndReadRate()
    {
        var conv = new ConversationData(Local(1, 10), true,
            [In(Local(1, 10)), Out(Local(1, 10, 5), read: true), Out(Local(1, 10, 10))], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(2, result.MessagesSent);
        Assert.Equal(1, result.MessagesReceived);
        Assert.Equal(0.5, result.ReadRate);
        Assert.Equal(2.0, result.SentReceivedRatio);
    }

    // Uptime: 19 dias de período com ~1,9 dia de downtime ≈ 90% (wall-clock, não útil).
    [Fact]
    public void Uptime_ReflectsWallClockDowntime()
    {
        var downtimes = new[] { new DowntimeInterval(From, From.AddHours(0.1 * (To - From).TotalHours)) };

        var result = NewCalculator().Compute(From, To, [], downtimes, 1);

        Assert.Equal(90, result.UptimePercent!.Value, precision: 5);
    }

    // Agregação por vendedor: somas de contagens, mediana recalculada sobre as amostras
    // unidas, média/h reagregada por soma de horas úteis, último envio pelo máximo e
    // uptime recomposto dos dois somáveis (200s cobertos, 20s fora = 90%).
    [Fact]
    public void Aggregate_MergesCountsAndSamples()
    {
        var lastA = Local(3, 10);
        var lastB = Local(5, 11);
        var saleOnce = new Dictionary<string, OutcomeTotals> { [OutcomeTypeCodes.Sale] = new(1, 1, 2) };
        var lostOnce = new Dictionary<string, OutcomeTotals> { [OutcomeTypeCodes.Lost] = new(1, 1, 3) };
        var a = new MetricsResult(2, 1, 1, 0, 10, 8, 5, 1, 1, saleOnce, 0, 100, 0, 10, lastA, [10], [10]);
        var b = new MetricsResult(2, 2, 0, 0, 6, 4, 3, 1, 0, lostOnce, 2, 100, 20, 10, lastB, [20, 30], [20]);

        var merged = MetricsResult.Aggregate([a, b]);

        Assert.Equal(4, merged.ConversationsStarted);
        Assert.Equal(3, merged.ConversationsAnswered);
        Assert.Equal(1, merged.ConversationsUnanswered);
        Assert.Equal(1, merged.OutboundConversationsStarted);
        Assert.Equal(2, merged.SilenceGaps);
        Assert.Equal(0.75, merged.ResponseRate);
        Assert.Equal(20, merged.MedianFirstResponseMinutes);
        Assert.Equal(2, merged.BanCount);
        Assert.Equal(90, merged.UptimePercent);
        Assert.Equal(0.8, merged.AvgSentPerBusinessHour);
        Assert.Equal(lastB, merged.LastOutboundMessageAt);
    }

    // Disparo = conversa iniciada pelo vendedor; Captação = disparo com qualquer resposta
    // do cliente. Nenhum dos dois entra em "conversas iniciadas" (que é só do cliente).
    [Fact]
    public void Shots_AndCaptures_AreCountedSeparately()
    {
        var engaged = new ConversationData(Local(1, 10), false,
            [Out(Local(1, 10)), In(Local(1, 11))], null);
        var coldShot = new ConversationData(Local(2, 10), false,
            [Out(Local(2, 10))], null);

        var result = NewCalculator().Compute(From, To, [engaged, coldShot], [], 0);

        Assert.Equal(0, result.ConversationsStarted);
        Assert.Equal(2, result.OutboundConversationsStarted);
        Assert.Equal(1, result.OutboundConversationsEngaged);
    }

    // Conversa iniciada pelo cliente sem resposta conta em "não respondidas".
    [Fact]
    public void UnansweredConversations_AreDerivedFromStartedMinusAnswered()
    {
        var answered = new ConversationData(Local(1, 10), true, [In(Local(1, 10)), Out(Local(1, 10, 30))], null);
        var ignored = new ConversationData(Local(2, 11), true, [In(Local(2, 11))], null);

        var result = NewCalculator().Compute(From, To, [answered, ignored], [], 0);

        Assert.Equal(1, result.ConversationsUnanswered);
    }

    // Média de mensagens por hora útil: 6 enviadas num período de 3h úteis = 2/h.
    [Fact]
    public void AvgMessagesPerBusinessHour_UsesEffectiveHours()
    {
        var messages = Enumerable.Range(0, 6).Select(i => Out(Local(1, 9, i * 5))).ToList();
        var conv = new ConversationData(Local(1, 9), false, messages, null);

        var result = NewCalculator().Compute(Local(1, 9), Local(1, 12), [conv], [], 0);

        Assert.Equal(3, result.EffectiveBusinessHours, precision: 5);
        Assert.Equal(2, result.AvgSentPerBusinessHour!.Value, precision: 5);
    }

    // Espera de resposta cobre TODA mensagem do cliente respondida (não só a primeira
    // da conversa): mínimo, máximo e média em minutos úteis.
    [Fact]
    public void ResponseWait_MinMaxAvg_OverEveryClientMessage()
    {
        var conv = new ConversationData(Local(1, 10), true,
            [
                In(Local(1, 10)), Out(Local(1, 10, 30)),   // 30 min
                In(Local(1, 11)), Out(Local(1, 11, 10)),   // 10 min
                In(Local(1, 12)), Out(Local(1, 13)),       // 60 min
            ], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(10, result.MinResponseMinutes);
        Assert.Equal(60, result.MaxResponseMinutes);
        Assert.Equal(100.0 / 3, result.AvgResponseMinutes!.Value, precision: 5);
    }

    // A data/hora da última mensagem enviada é o maior timestamp outbound do período.
    [Fact]
    public void LastOutboundMessageAt_IsMaxOutboundTimestamp()
    {
        var conv = new ConversationData(Local(1, 10), true,
            [In(Local(1, 10)), Out(Local(1, 10, 30)), Out(Local(2, 16)), In(Local(3, 9))], null);

        var result = NewCalculator().Compute(From, To, [conv], [], 0);

        Assert.Equal(Local(2, 16), result.LastOutboundMessageAt);
    }
}
