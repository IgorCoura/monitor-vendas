using System.Net.Http.Json;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

// A espera de resposta é medida da mensagem do vendedor para trás, até a primeira
// mensagem do cliente ainda não respondida. O caso que só aparece ponta a ponta é
// a virada do dia: o cliente escreve à noite e a resposta vem na manhã seguinte,
// já dentro do período — a pergunta está FORA da janela e precisa ser carregada
// mesmo assim, senão a resposta da manhã não tem a que se referir.
public class ResponseWaitTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000cd01");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000cd01");
    private static readonly Guid ContactId = Guid.Parse("c0a10000-0000-0000-0000-00000000cd01");
    private static readonly Guid ConversationId = Guid.Parse("c0f10000-0000-0000-0000-00000000cd01");

    // 01/07/2026 é quarta e 02/07 é quinta — dias úteis, expediente 9h–18h.
    private static DateTime Local(int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 7, day, hour, minute, 0, DateTimeKind.Unspecified), SaoPaulo);

    private async Task SeedAsync(DateTime askedAt, DateTime answeredAt)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Local(1, 0) });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900003333",
                InstanceName = "mv-espera",
                Status = NumberStatus.Active,
                CreatedAt = Local(1, 0),
            });
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = NumberId,
                State = "open",
                ResultingStatus = NumberStatus.Active,
                OccurredAt = Local(1, 0),
            });
            db.Add(new Contact { Id = ContactId, RemoteJid = "5511700003333@s.whatsapp.net", CreatedAt = Local(1, 0) });
            db.Add(new Conversation
            {
                Id = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = ContactId,
                StartedByContact = true,
                StartedAt = askedAt,
                LastMessageAt = answeredAt,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = "pergunta",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Timestamp = askedAt,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = "resposta",
                Direction = MessageDirection.Outbound,
                Type = "conversation",
                Timestamp = answeredAt,
            });

            return Task.CompletedTask;
        });
    }

    // O dia 2 inteiro: a pergunta da véspera fica fora da janela, a resposta dentro.
    private async Task<MetricsDto> DayTwoTotalsAsync()
    {
        var report = await Client.GetFromJsonAsync<SellerReportDto>(
            $"/api/v1/reports/sellers/{SellerId}?from={Local(2, 0):O}&to={Local(3, 0):O}");

        return report!.Totals;
    }

    // Cliente escreve 23h (fora do expediente) e é respondido às 9h30 do dia
    // seguinte: só os 30 minutos dentro do expediente contam, e a amostra existe —
    // carregar apenas as mensagens do período deixava essa resposta sem pergunta.
    [Fact]
    public async Task AnswerNextMorning_MeasuresOnlyTheBusinessMinutes()
    {
        await SeedAsync(askedAt: Local(1, 23), answeredAt: Local(2, 9, 30));

        var totals = await DayTwoTotalsAsync();

        Assert.Equal(1, totals.ResponseSamplesCount);
        Assert.Equal(30, totals.AvgResponseMinutes);
        Assert.Equal(30, totals.MinResponseMinutes);
        Assert.Equal(30, totals.MaxResponseMinutes);
    }

    // A pergunta pode ser bem mais velha que a janela de carga (cliente esperando
    // desde a semana passada): quem garante a amostra aí é a mensagem de fronteira,
    // que passou a carregar a DIREÇÃO real. Gravada sempre como "do vendedor", ela
    // apagava a espera de quem estava sem resposta havia dias.
    [Fact]
    public async Task AnswerToAnOldUnansweredMessage_StillCounts()
    {
        // Segunda 29/06 às 17h, respondida quinta 02/07 às 9h30 — fora da janela de
        // carga (2 dias), então quem a traz é a fronteira. 1h da segunda + 9h da
        // terça + 9h da quarta + 30 min da quinta = 1170 minutos úteis, sem fim de
        // semana no meio para depender da config de sábado.
        await SeedAsync(askedAt: Local(1, 17).AddDays(-2), answeredAt: Local(2, 9, 30));

        var totals = await DayTwoTotalsAsync();

        Assert.Equal(1, totals.ResponseSamplesCount);
        Assert.Equal(1170, totals.AvgResponseMinutes);
    }
}
