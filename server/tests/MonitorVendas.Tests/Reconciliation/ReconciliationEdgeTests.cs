using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Reconciliation;

// A reconciliação é a rede de segurança: ela sintetiza eventos a partir do que a
// Evolution ainda tem. Sintetizar demais é pior que de menos — vira estado falso
// e mensagem duplicada.
public class ReconciliationEdgeTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000e9");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000e9");
    private static readonly Guid ContactId = Guid.Parse("c0a10000-0000-0000-0000-0000000000e9");
    private static readonly Guid ConversationId = Guid.Parse("c0117e00-0000-0000-0000-0000000000e9");
    private const string Instance = "mv-recon-borda";
    private const string ClientJid = "5511966665555@s.whatsapp.net";

    private async Task SeedAsync(NumberStatus status = NumberStatus.Active)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900008888",
                InstanceName = Instance,
                Status = status,
                CreatedAt = Start,
                LastReconciledAt = DateTime.UtcNow.AddHours(-1),
            });
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = NumberId,
                State = "open",
                ResultingStatus = NumberStatus.Active,
                OccurredAt = Start,
            });

            return Task.CompletedTask;
        });
    }

    private Task<int> RunAsync() =>
        Factory.Services.GetRequiredService<IReconciliationService>().RunOnceAsync();

    // Estado que a Evolution não deveria devolver (instância removida, resposta de
    // erro) não vira evento: sintetizar um "close" aqui abriria downtime falso e
    // puniria o vendedor por um canal que está no ar.
    [Fact]
    public async Task UnknownState_DoesNotSynthesizeAnything()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"refused"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");

        Assert.Equal(0, await RunAsync());
        Assert.Equal(0, await InDbAsync(db => db.Set<WebhookEvent>().CountAsync()));
        Assert.Equal(NumberStatus.Active, await InDbAsync(db =>
            db.Set<WhatsappNumber>().Where(n => n.Id == NumberId).Select(n => n.Status).SingleAsync()));
    }

    // Mensagem que já está gravada não volta pela varredura mesmo sem o evento
    // bruto na fila (fila limpa por retenção): a segunda checagem é o banco.
    [Fact]
    public async Task MessageAlreadyStored_IsNotImportedAgain()
    {
        await SeedAsync();
        await SeedAsync(db =>
        {
            db.Add(new Contact { Id = ContactId, RemoteJid = ClientJid, PushName = "Cliente", CreatedAt = Start });
            db.Add(new Conversation
            {
                Id = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = ContactId,
                StartedByContact = true,
                StartedAt = Start,
                LastMessageAt = Start,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = "JA-1",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "oi",
                Timestamp = DateTime.UtcNow.AddMinutes(-10),
            });

            return Task.CompletedTask;
        });

        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": {
                "records": [
                  {
                    "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "JA-1" },
                    "message": { "conversation": "oi" },
                    "messageType": "conversation",
                    "messageTimestamp": {{DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()}}
                  },
                  {
                    "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "SEM-DATA" },
                    "message": { "conversation": "sem timestamp" },
                    "messageType": "conversation"
                  }
                ]
              }
            }
            """);

        Assert.Equal(0, await RunAsync());
        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync()));
    }
}
