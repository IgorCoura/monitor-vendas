using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Tests.Infrastructure;

// Nos testes os serviços em background ficam desligados e o trabalho é disparado
// à mão — determinismo que vale a pena. O preço é que o laço que roda em
// produção nunca é exercido: um erro nele só apareceria com o sistema no ar.
public class BackgroundLoopTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000ba");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000ba");
    private const string Instance = "mv-laco";

    private async Task SeedNumberAsync(NumberStatus status = NumberStatus.Active)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow.AddDays(-30) });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900004444",
                InstanceName = Instance,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddDays(-30),
            });
            // Já pareou um dia: é o que faz a reconciliação varrer este número.
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = NumberId,
                State = "open",
                ResultingStatus = NumberStatus.Active,
                OccurredAt = DateTime.UtcNow.AddDays(-2),
            });

            return Task.CompletedTask;
        });
    }

    private async Task RunUntilAsync(IHostedService service, Func<Task<bool>> done)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.StartAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested && !await done())
                await Task.Delay(50, cts.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // O ciclo de reconciliação varre a Evolution e ainda processa o que sintetizou:
    // sem essa segunda parte, o evento ficaria na fila até a próxima passada do
    // processador de webhooks.
    [Fact]
    public async Task ReconciliationLoop_RecoversAndProcessesInTheSamePass()
    {
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": {
                "records": [
                  {
                    "key": { "remoteJid": "5511922221111@s.whatsapp.net", "fromMe": false, "id": "LOOP-R1" },
                    "pushName": "Cliente",
                    "message": { "conversation": "oi" },
                    "messageType": "conversation",
                    "messageTimestamp": {{DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds()}}
                  }
                ]
              }
            }
            """);

        var service = new ReconciliationBackgroundService(
            Factory.Services.GetRequiredService<IReconciliationService>(),
            Factory.Services.GetRequiredService<IWebhookProcessor>(),
            Options.Create(new ReconciliationOptions { Enabled = true, IntervalMinutes = 1 }),
            Factory.Services.GetRequiredService<ILogger<ReconciliationBackgroundService>>());

        await RunUntilAsync(service, async () => await InDbAsync(db => db.Set<Message>().CountAsync()) > 0);

        var recovered = await InDbAsync(db => db.Set<Message>().AsNoTracking().SingleAsync());
        Assert.Equal("LOOP-R1", recovered.WaMessageId);
    }

    // O laço da agregação consome os dias marcados como sujos e fecha o agregado
    // diário — é dele que saem os relatórios de período longo.
    [Fact]
    public async Task AggregationLoop_ClosesTheDirtyDays()
    {
        await SeedNumberAsync();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        await SeedAsync(db =>
        {
            db.Add(new DirtyMetricsDay { WhatsappNumberId = NumberId, Day = day, MarkedAt = DateTime.UtcNow });
            return Task.CompletedTask;
        });

        var service = new MetricsAggregationBackgroundService(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetricsOptions { AggregationEnabled = true, AggregationIntervalSeconds = 10 }),
            Factory.Services.GetRequiredService<ILogger<MetricsAggregationBackgroundService>>());

        await RunUntilAsync(service, async () => await InDbAsync(db => db.Set<DirtyMetricsDay>().CountAsync()) == 0);

        Assert.Equal(1, await InDbAsync(db => db.Set<DailyNumberMetrics>().CountAsync(m => m.Day == day)));
    }

    // O laço do envio de contatos manda o que está pendente e marca como enviado —
    // a lista congelada no pedido sai por ele, uma mensagem por vez.
    [Fact]
    public async Task ContactShareLoop_SendsWhatIsPending()
    {
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"SENT-1"}}""");

        var shareId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            db.Add(new ContactShare
            {
                Id = shareId,
                SenderNumberId = NumberId,
                Destination = "5511911112222",
                TotalContacts = 1,
                Status = ContactShareStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            });
            db.Add(new ContactShareMessage
            {
                Id = Guid.NewGuid(),
                ContactShareId = shareId,
                Sequence = 1,
                Body = "Maria - 5511977776666",
            });

            return Task.CompletedTask;
        });

        var service = new ContactShareBackgroundService(
            Factory.Services.GetRequiredService<IContactShareSender>(),
            Options.Create(new ContactShareOptions { Enabled = true, IntervalSeconds = 1, MinDelaySeconds = 0, MaxDelaySeconds = 0 }),
            Factory.Services.GetRequiredService<ILogger<ContactShareBackgroundService>>());

        await RunUntilAsync(service, async () =>
            await InDbAsync(db => db.Set<ContactShare>().CountAsync(s => s.Id == shareId && s.Status == ContactShareStatus.Completed)) > 0);

        var sent = await InDbAsync(db => db.Set<ContactShareMessage>().AsNoTracking().SingleAsync());
        Assert.NotNull(sent.SentAt);
        Assert.Equal("SENT-1", sent.WaMessageId);
    }
}
