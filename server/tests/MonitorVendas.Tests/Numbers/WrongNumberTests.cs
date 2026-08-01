using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// Repareamento divergente: a instância já é de um cadastro, mas quem escaneou o
// QR foi outro WhatsApp. Deixar seguir misturaria o histórico de dois números no
// mesmo vendedor — o cadastro vai para quarentena e a sessão cai.
public class WrongNumberTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000d1");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000d1");
    private static readonly Guid ContactId = Guid.Parse("c0a10000-0000-0000-0000-0000000000d1");
    private static readonly Guid ConversationId = Guid.Parse("c0117e00-0000-0000-0000-0000000000d1");
    private const string Instance = "mv-divergente";
    private const string Registered = "5511968608425";
    private const string Intruder = "5511911112222";
    private const string ClientJid = "5511955554444@s.whatsapp.net";

    // Histórico legítimo do número antes de alguém parear com o WhatsApp errado.
    private async Task SeedAsync(NumberStatus status = NumberStatus.Active)
    {
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");

        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = Registered,
                InstanceName = Instance,
                Status = status,
                CreatedAt = Start,
            });
            db.Add(new Contact { Id = ContactId, RemoteJid = ClientJid, PushName = "Maria", CreatedAt = Start });
            db.Add(new Conversation
            {
                Id = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = ContactId,
                StartedByContact = false,
                StartedAt = Start,
                LastMessageAt = Start,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = "OLD-1",
                Direction = MessageDirection.Outbound,
                Type = "conversation",
                Text = "bom dia",
                Timestamp = Start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task PostAsync(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();
    }

    private Task ConnectAsync(string phone) => PostAsync(new
    {
        @event = "connection.update",
        instance = Instance,
        data = new { instance = Instance, wuid = $"{phone}@s.whatsapp.net", state = "open", statusReason = 200 },
    });

    private Task<WhatsappNumber> NumberAsync() =>
        InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));

    // WhatsApp diferente do cadastrado: quarentena e logout imediato, para o
    // intruso não continuar despejando a conversa dele no cadastro errado.
    [Fact]
    public async Task Connect_WithADifferentWhatsapp_QuarantinesAndLogsOut()
    {
        await SeedAsync();

        await ConnectAsync(Intruder);

        Assert.Equal(NumberStatus.WrongNumber, (await NumberAsync()).Status);
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.Contains($"/instance/logout/{Instance}", StringComparison.OrdinalIgnoreCase));

        var events = await InDbAsync(db => db.Set<NumberStatusEvent>()
            .Where(e => e.WhatsappNumberId == NumberId).ToListAsync());
        Assert.Contains(events, e => e.State == "wrong-number" && e.ResultingStatus == NumberStatus.WrongNumber);
    }

    // O mesmo número escrito de outro jeito (sem o 9º dígito) é o mesmo WhatsApp:
    // acusar divergência aqui derrubaria a sessão de quem está certo.
    [Fact]
    public async Task Connect_WithTheSameNumberInAnotherShape_IsNotDivergent()
    {
        await SeedAsync(NumberStatus.Disconnected);

        await ConnectAsync("551168608425");

        Assert.Equal(NumberStatus.Active, (await NumberAsync()).Status);
        Assert.DoesNotContain(FakeEvolution.Requests, r =>
            r.Path.Contains("/instance/logout/", StringComparison.OrdinalIgnoreCase));
    }

    // Em quarentena nada da instância vira dado: mensagem nova, ack e etiqueta são
    // descartados. É o histórico do WhatsApp errado batendo na porta.
    [Fact]
    public async Task WhileQuarantined_EverythingFromTheInstanceIsDiscarded()
    {
        await SeedAsync();
        await ConnectAsync(Intruder);

        await PostAsync(new
        {
            @event = "messages.upsert",
            instance = Instance,
            data = new
            {
                key = new { remoteJid = ClientJid, fromMe = false, id = "WRONG-1" },
                pushName = "Cliente",
                message = new { conversation = "oi" },
                messageType = "conversation",
                messageTimestamp = 1785000000,
            },
        });

        await PostAsync(new
        {
            @event = "messages.update",
            instance = Instance,
            data = new { keyId = "OLD-1", status = "READ" },
        });

        await PostAsync(new
        {
            @event = "labels.association",
            instance = Instance,
            data = new { chatId = ClientJid, labelId = "1", type = "add" },
        });

        // Só a mensagem legítima anterior à quarentena continua lá, e intocada.
        var message = await InDbAsync(db => db.Set<Message>().AsNoTracking().SingleAsync());
        Assert.Equal("OLD-1", message.WaMessageId);
        Assert.Null(message.ReadAt);
        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationLabel>().CountAsync()));
    }

    // Evento sem `wuid` (a reconciliação sintetiza assim) não pode ser lido como
    // divergência: seria derrubar todo mundo a cada varredura.
    [Fact]
    public async Task Connect_WithoutWuid_IsTreatedAsANormalConnection()
    {
        await SeedAsync(NumberStatus.Disconnected);

        await PostAsync(new
        {
            @event = "connection.update",
            instance = Instance,
            data = new { instance = Instance, state = "open" },
        });

        Assert.Equal(NumberStatus.Active, (await NumberAsync()).Status);
    }

    // 403 em número já dado como banido permanente não rebaixa a decisão manual
    // para temporária — sair do ban permanente exige confirmação de quem opera.
    [Fact]
    public async Task Ban403_OnAPermanentlyBannedNumber_KeepsItPermanent()
    {
        await SeedAsync(NumberStatus.BannedPermanent);

        await PostAsync(new
        {
            @event = "connection.update",
            instance = Instance,
            data = new { instance = Instance, state = "close", statusReason = 403 },
        });

        Assert.Equal(NumberStatus.BannedPermanent, (await NumberAsync()).Status);
    }

    // `close` sem motivo (o que a reconciliação sintetiza ao ver a instância
    // fechada) não pode transformar um ban registrado em simples desconexão.
    [Fact]
    public async Task CloseWithoutReason_DoesNotDowngradeARegisteredBan()
    {
        await SeedAsync(NumberStatus.BannedTemporary);

        await PostAsync(new
        {
            @event = "connection.update",
            instance = Instance,
            data = new { instance = Instance, state = "close" },
        });

        Assert.Equal(NumberStatus.BannedTemporary, (await NumberAsync()).Status);
    }
}
