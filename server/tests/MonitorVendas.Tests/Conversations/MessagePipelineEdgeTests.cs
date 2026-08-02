using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Conversations;

// O payload da Evolution muda de forma entre versões e entre eventos reais e
// sintetizados pela reconciliação. Campo em formato não previsto vira mensagem
// perdida — ou pior, mensagem com a hora errada, que desloca toda a métrica.
public class MessagePipelineEdgeTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000c9");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000c9");
    private const string Instance = "mv-borda";
    private const string ClientJid = "5511944445555@s.whatsapp.net";

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900007777",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = Start,
            });

            return Task.CompletedTask;
        });
    }

    // Entra direto na fila: alguns destes payloads seriam recusados na porta, e o
    // que se quer medir aqui é o handler.
    private async Task ProcessAsync(string payload)
    {
        await SeedAsync(db =>
        {
            db.Add(new WebhookEvent
            {
                InstanceName = Instance,
                EventType = "MESSAGES_UPSERT",
                Payload = payload,
                ReceivedAt = Start.AddHours(1),
            });

            return Task.CompletedTask;
        });

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();
    }

    private Task<List<Message>> MessagesAsync() =>
        InDbAsync(db => db.Set<Message>().AsNoTracking().ToListAsync());

    // Evento sem `data`, sem `key` ou sem os ids da chave é descartado sem erro:
    // são formas que a Evolution manda e que não descrevem mensagem nenhuma.
    [Fact]
    public async Task Upsert_WithoutUsableKey_IsDiscarded()
    {
        await SeedAsync();

        await ProcessAsync("""{"event":"messages.upsert","instance":"mv-borda"}""");
        await ProcessAsync("""{"event":"messages.upsert","instance":"mv-borda","data":{"pushName":"Cliente"}}""");
        await ProcessAsync("""
            {"event":"messages.upsert","instance":"mv-borda","data":{"key":{"fromMe":false},"message":{"conversation":"oi"}}}
            """);

        Assert.Empty(await MessagesAsync());
        // Nenhum deles pode ter ficado marcado como falha: não são erro, são ruído.
        Assert.Equal(0, await InDbAsync(db => db.Set<WebhookEvent>().CountAsync(e => e.Attempts > 0)));
    }

    // A mesma mensagem chegando duas vezes (webhook reenviado + reconciliação) é
    // gravada uma vez só — a contagem de mensagens é métrica de desempenho.
    [Fact]
    public async Task Upsert_Twice_StoresOnlyOnce()
    {
        await SeedAsync();
        var payload = $$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "DUP-1" },
                "pushName": "Cliente",
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "messageTimestamp": 1785067200
              }
            }
            """;

        await ProcessAsync(payload);
        await ProcessAsync(payload);

        Assert.Single(await MessagesAsync());
    }

    // Timestamp como string (a Evolution alterna entre número e string) precisa ser
    // lido: cair no fallback colocaria a mensagem na hora em que o evento foi
    // processado, e não na hora em que o cliente escreveu.
    [Fact]
    public async Task Upsert_WithStringTimestamp_KeepsTheRealTime()
    {
        await SeedAsync();

        await ProcessAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "STR-1" },
                "pushName": "Cliente",
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "messageTimestamp": "1785067200"
              }
            }
            """);

        var message = Assert.Single(await MessagesAsync());
        Assert.Equal(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), message.Timestamp);
    }

    // Sem timestamp (ou em formato ilegível) vale a hora em que o evento chegou:
    // mensagem sem hora nenhuma não teria como entrar em métrica alguma.
    [Fact]
    public async Task Upsert_WithoutTimestamp_FallsBackToReception()
    {
        await SeedAsync();

        await ProcessAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "NOTS-1" },
                "pushName": "Cliente",
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "messageTimestamp": { "low": 123 }
              }
            }
            """);

        var message = Assert.Single(await MessagesAsync());
        Assert.Equal(Start.AddHours(1), message.Timestamp);
    }

    // A duração do áudio também vem ora número, ora string — é ela que dá o
    // "[áudio de 45s]" na transcrição e o teto de segundos por conversa.
    [Fact]
    public async Task Upsert_WithStringAudioDuration_IsRead()
    {
        await SeedAsync();

        await ProcessAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{ClientJid}}", "fromMe": false, "id": "AUD-1" },
                "pushName": "Cliente",
                "message": { "audioMessage": { "seconds": "45", "mimetype": "audio/ogg" } },
                "messageType": "audioMessage",
                "messageTimestamp": 1785067200
              }
            }
            """);

        var message = Assert.Single(await MessagesAsync());
        Assert.Equal(45, message.DurationSeconds);
    }
}
