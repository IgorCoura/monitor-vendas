using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Webhooks;

// A fila de eventos brutos é o funil por onde passa tudo o que vem do WhatsApp.
// Um evento ruim não pode parar a fila, e nem ser retentado para sempre.
public class WebhookProcessorTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000f1");
    private const string Instance = "mv-fila";

    private async Task SeedNumberAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = Guid.NewGuid(),
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = Start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task EnqueueAsync(string eventType, string payload, string? dedupeKey = null)
    {
        await SeedAsync(db =>
        {
            db.Add(new WebhookEvent
            {
                InstanceName = Instance,
                EventType = eventType,
                Payload = payload,
                DedupeKey = dedupeKey,
                ReceivedAt = DateTime.UtcNow,
            });

            return Task.CompletedTask;
        });
    }

    private static string UpsertPayload(string id) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{Instance}}",
          "data": {
            "key": { "remoteJid": "5511977776666@s.whatsapp.net", "fromMe": false, "id": "{{id}}" },
            "pushName": "Cliente",
            "message": { "conversation": "oi" },
            "messageType": "conversation",
            "messageTimestamp": 1785000000
          }
        }
        """;

    private Task<int> ProcessAsync() =>
        Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();

    private Task<WebhookEvent> EventAsync(string eventType) =>
        InDbAsync(db => db.Set<WebhookEvent>().AsNoTracking().FirstAsync(e => e.EventType == eventType));

    // Evento quebrado registra o erro e conta UMA tentativa, mas não trava a fila:
    // o evento bom que veio depois é processado na mesma passada. (Regressão: o
    // laço repescava o que falhou e gastava as 5 tentativas de uma vez.)
    [Fact]
    public async Task ABrokenEvent_DoesNotStopTheQueue()
    {
        await SeedNumberAsync();
        await EnqueueAsync("MESSAGES_UPSERT", "{ isso não é json");
        await EnqueueAsync("MESSAGES_UPSERT", UpsertPayload("OK-1"), $"{Instance}:MESSAGES_UPSERT:OK-1");

        await ProcessAsync();

        var broken = await InDbAsync(db => db.Set<WebhookEvent>().AsNoTracking().FirstAsync(e => e.DedupeKey == null));
        Assert.Equal(1, broken.Attempts);
        Assert.NotNull(broken.Error);
        Assert.Null(broken.ProcessedAt);

        var stored = await InDbAsync(db => db.Set<Message>().AsNoTracking().SingleAsync());
        Assert.Equal("OK-1", stored.WaMessageId);
    }

    // Depois de 5 tentativas o evento sai de circulação: insistir para sempre
    // queimaria a passada inteira em algo que nunca vai processar.
    [Fact]
    public async Task AfterFiveAttempts_TheEventIsLeftAlone()
    {
        await SeedNumberAsync();
        await EnqueueAsync("MESSAGES_UPSERT", "{ isso não é json");

        for (var i = 0; i < 7; i++)
            await ProcessAsync();

        Assert.Equal(5, (await EventAsync("MESSAGES_UPSERT")).Attempts);
    }

    // Tipo de evento sem handler (a Evolution manda dezenas que não interessam) é
    // marcado como processado e some da fila — não é erro.
    [Fact]
    public async Task AnEventWithoutHandler_IsMarkedAsDone()
    {
        await EnqueueAsync("PRESENCE_UPDATE", """{"event":"presence.update","data":{}}""");

        Assert.Equal(1, await ProcessAsync());

        var evt = await EventAsync("PRESENCE_UPDATE");
        Assert.NotNull(evt.ProcessedAt);
        Assert.Null(evt.Error);
        Assert.Equal(0, evt.Attempts);
    }

    // O serviço em background é quem drena a fila em produção — nos testes ele fica
    // desligado, então o laço dele só é exercido aqui.
    [Fact]
    public async Task TheBackgroundLoop_DrainsTheQueue()
    {
        await SeedNumberAsync();
        await EnqueueAsync("MESSAGES_UPSERT", UpsertPayload("LOOP-1"), $"{Instance}:MESSAGES_UPSERT:LOOP-1");

        var service = new WebhookProcessorBackgroundService(
            Factory.Services.GetRequiredService<IWebhookProcessor>(),
            Options.Create(new WebhookOptions { ProcessorIntervalSeconds = 1 }),
            Factory.Services.GetRequiredService<ILogger<WebhookProcessorBackgroundService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested && await InDbAsync(db => db.Set<Message>().CountAsync()) == 0)
                await Task.Delay(50, cts.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var stored = await InDbAsync(db => db.Set<Message>().AsNoTracking().SingleAsync());
        Assert.Equal("LOOP-1", stored.WaMessageId);
    }
}
