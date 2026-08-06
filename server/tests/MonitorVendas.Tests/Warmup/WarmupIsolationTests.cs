using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Warmup;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Warmup;

// O isolamento é a peça mais crítica do aquecimento: se o tráfego do pool
// entrar no pipeline do produto, contamina tempo de resposta, conversão,
// ranking, carteira e a análise de IA — e não há como separar depois.
public class WarmupIsolationTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string InstanceA = "mv-5511900001111";
    private const string InstanceB = "mv-5511900002222";
    private const string PhoneA = "5511900001111";
    private const string PhoneB = "5511900002222";
    private const string Period = "from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z";

    private Guid _numberA;
    private Guid _numberB;

    private static long Ts(int day, int hour) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hour, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task<Guid> AddNumberAsync(Guid sellerId, string phone)
    {
        var number = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{sellerId}/numbers", new { phone }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return number.GetProperty("number").GetProperty("id").GetGuid();
    }

    private async Task SeedAsync(bool bothInPool)
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Post, "/settings/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sellerId = seller.GetProperty("id").GetGuid();

        _numberA = await AddNumberAsync(sellerId, PhoneA);
        _numberB = await AddNumberAsync(sellerId, PhoneB);

        foreach (var instance in new[] { InstanceA, InstanceB })
            await PostWebhookAsync($$"""
                { "event": "connection.update", "instance": "{{instance}}",
                  "data": { "state": "open", "statusReason": 200 }, "date_time": "2026-06-30T12:00:00Z" }
                """);

        await SeedAsync(db =>
        {
            db.Add(new WarmupPeer
            {
                Id = Guid.NewGuid(), WhatsappNumberId = _numberA,
                Persona = WarmupPersona.Seco, JoinedAt = DateTime.UtcNow.AddDays(-10),
            });

            if (bothInPool)
            {
                db.Add(new WarmupPeer
                {
                    Id = Guid.NewGuid(), WhatsappNumberId = _numberB,
                    Persona = WarmupPersona.Falante, JoinedAt = DateTime.UtcNow.AddDays(-10),
                });
            }

            return Task.CompletedTask;
        });
    }

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    // Mensagem que A manda para B, os dois no pool.
    private Task PoolMessageAsync(string id) =>
        PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{InstanceA}}",
              "data": {
                "key": { "remoteJid": "{{PhoneB}}@s.whatsapp.net", "fromMe": true, "id": "{{id}}" },
                "message": { "conversation": "bom dia, chegou o material da turma?" },
                "messageType": "conversation", "messageTimestamp": {{Ts(5, 13)}}
              }
            }
            """);

    private Task ProcessAsync() =>
        Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();

    // Conversa entre dois números do pool NÃO vira Message, Conversation nem
    // Contact: fora das métricas por completo.
    [Fact]
    public async Task PoolTraffic_NeverEntersTheProductPipeline()
    {
        await SeedAsync(bothInPool: true);

        await PoolMessageAsync("W-1");
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Set<Conversation>().CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Set<Contact>().CountAsync()));
    }

    // ...e por consequência não aparece no ranking: o vendedor não ganha
    // mensagem enviada por conversar com o colega.
    [Fact]
    public async Task PoolTraffic_DoesNotShowUpInTheRanking()
    {
        await SeedAsync(bothInPool: true);
        await PoolMessageAsync("W-2");
        await ProcessAsync();

        var ranking = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/ranking?{Period}");

        var entry = ranking.EnumerateArray().FirstOrDefault();
        if (entry.ValueKind != JsonValueKind.Undefined)
            Assert.Equal(0, entry.GetProperty("metrics").GetProperty("messagesSent").GetInt32());
    }

    // O filtro só vale entre DOIS números do pool. Mensagem de um número do
    // pool para um aluno de verdade continua sendo métrica — se este teste
    // quebrar, o aquecimento está apagando o produto.
    [Fact]
    public async Task MessageToARealContact_StillCounts()
    {
        await SeedAsync(bothInPool: true);

        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{InstanceA}}",
              "data": {
                "key": { "remoteJid": "5511777770000@s.whatsapp.net", "fromMe": false, "id": "R-1" },
                "message": { "conversation": "queria saber da pos em gestao" },
                "messageType": "conversation", "pushName": "Aluno", "messageTimestamp": {{Ts(5, 14)}}
              }
            }
            """);
        await ProcessAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.Set<Conversation>().CountAsync()));
    }

    // Um dos dois fora do pool: é conversa normal e entra nas métricas. O
    // filtro exige os DOIS lados dentro.
    [Fact]
    public async Task WhenOnlyOneSideIsInThePool_ItIsNormalTraffic()
    {
        await SeedAsync(bothInPool: false);

        await PoolMessageAsync("W-3");
        await ProcessAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync()));
    }

    // Número que saiu do pool volta a ter as mensagens contadas: o filtro olha
    // quem está no pool AGORA.
    [Fact]
    public async Task AfterLeavingThePool_TrafficCountsAgain()
    {
        await SeedAsync(bothInPool: true);

        await InDbAsync(async db =>
        {
            await db.Set<WarmupPeer>().Where(p => p.WhatsappNumberId == _numberB)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, DateTime.UtcNow));
            return 0;
        });

        await PoolMessageAsync("W-4");
        await ProcessAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync()));
    }

    // REGRESSÃO (encontrada rodando contra o WhatsApp de verdade em 2026-08-05):
    // o WhatsApp entregou o tráfego do pool endereçado por LID
    // (`222904574804050@lid`), que não contém telefone nenhum. O filtro comparava
    // só telefone, nada casou, e as mensagens do aquecimento viraram conversa de
    // aluno no relatório. O id da mensagem é o mesmo nos dois lados e é ele que
    // fecha esse buraco.
    [Fact]
    public async Task PoolTrafficAddressedByLid_IsStillKeptOutOfTheMetrics()
    {
        await SeedAsync(bothInPool: true);

        var peerA = await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking()
            .FirstAsync(p => p.WhatsappNumberId == _numberA));
        var peerB = await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking()
            .FirstAsync(p => p.WhatsappNumberId == _numberB));
        var conversationId = Guid.NewGuid();

        await SeedAsync(db =>
        {
            db.Add(new WarmupConversation
            {
                Id = conversationId, PeerAId = peerA.Id, PeerBId = peerB.Id,
                Theme = "combinar o almoço", Status = WarmupConversationStatus.Running,
                CreatedAt = DateTime.UtcNow,
            });
            db.Add(new WarmupTurn
            {
                Id = Guid.NewGuid(), ConversationId = conversationId, Sequence = 1, FromPeerId = peerA.Id,
                Text = "bora almoçar?", ScheduledAt = DateTime.UtcNow, SentAt = DateTime.UtcNow,
                WaMessageId = "LID-1",
            });
            return Task.CompletedTask;
        });

        // A cópia que chega no destinatário: mesmo id, endereçada por LID.
        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{InstanceB}}",
              "data": {
                "key": { "remoteJid": "222904574804050@lid", "fromMe": false, "id": "LID-1",
                         "addressingMode": "lid" },
                "message": { "conversation": "bora almoçar?" },
                "messageType": "conversation", "messageTimestamp": {{Ts(5, 15)}}
              }
            }
            """);
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Set<Contact>().CountAsync()));
    }

    // REGRESSÃO: no modo LID o `remoteJid` não tem telefone e o `remoteJidAlt`
    // tem. Gravar o contato pelo LID cria um segundo cadastro da mesma pessoa,
    // sem número — e a exportação de contatos, que existe para produzir
    // "Nome - 5511999998888", sai sem o número.
    [Fact]
    public async Task WhenAddressedByLid_TheContactKeepsThePhone()
    {
        await SeedAsync(bothInPool: true);

        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{InstanceA}}",
              "data": {
                "key": { "remoteJid": "42309773226234@lid", "fromMe": false, "id": "ALT-1",
                         "remoteJidAlt": "5511777770000@s.whatsapp.net", "addressingMode": "lid" },
                "message": { "conversation": "queria saber da pos em gestao" },
                "messageType": "conversation", "pushName": "Aluno", "messageTimestamp": {{Ts(5, 16)}}
              }
            }
            """);
        await ProcessAsync();

        var contact = await InDbAsync(db => db.Set<Contact>().AsNoTracking().SingleAsync());
        Assert.Equal("5511777770000@s.whatsapp.net", contact.RemoteJid);
    }

    // Ponta a ponta da consolidação: o contato que entrou por LID volta a ser o
    // contato de telefone, com o histórico junto.
    [Fact]
    public async Task LidConsolidation_TurnsTheLidContactBackIntoAPhoneContact()
    {
        await SeedAsync(bothInPool: false);

        // Mesma pessoa, duas vezes: uma pelo telefone e outra pelo LID.
        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{InstanceA}}",
              "data": {
                "key": { "remoteJid": "5511777770000@s.whatsapp.net", "fromMe": false, "id": "P-1" },
                "message": { "conversation": "oi, queria saber da pos" },
                "messageType": "conversation", "pushName": "Aluno", "messageTimestamp": {{Ts(5, 10)}}
              }
            }
            """);
        // Este chegou antes da correção do LID: o remoteJidAlt existe, mas o
        // contato foi gravado pelo LID. Simulamos gravando à mão.
        await ProcessAsync();

        var lidContactId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            db.Add(new Contact
            {
                Id = lidContactId, RemoteJid = "222904574804050@lid",
                PushName = "Aluno", CreatedAt = DateTime.UtcNow,
            });
            db.Add(new Conversation
            {
                Id = Guid.NewGuid(), WhatsappNumberId = _numberA, ContactId = lidContactId,
                SellerId = Guid.NewGuid(), StartedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
            });
            db.Add(new MonitorVendas.Api.Features.Webhooks.WebhookEvent
            {
                InstanceName = InstanceA, EventType = "MESSAGES_UPSERT",
                DedupeKey = $"{InstanceA}:MESSAGES_UPSERT:LIDMAP",
                ReceivedAt = DateTime.UtcNow,
                Payload = """
                    {"event":"messages.upsert","data":{"key":{"remoteJid":"222904574804050@lid",
                     "remoteJidAlt":"5511777770000@s.whatsapp.net","id":"LIDMAP","fromMe":false}}}
                    """,
            });
            return Task.CompletedTask;
        });

        var preview = await Client.GetFromJsonAsync<JsonElement>("/api/v1/contacts/lid-consolidation");
        Assert.Equal(1, preview.GetProperty("merges").GetArrayLength());

        var applied = await Client.PostAsync("/api/v1/contacts/lid-consolidation", null);
        applied.EnsureSuccessStatusCode();

        // O contato LID sumiu e as conversas dele foram para o de telefone.
        Assert.False(await InDbAsync(db => db.Set<Contact>().AnyAsync(c => c.Id == lidContactId)));
        Assert.Equal(0, await InDbAsync(db => db.Set<Contact>().CountAsync(c => c.RemoteJid.EndsWith("@lid"))));
        Assert.Equal(2, await InDbAsync(db => db.Set<Conversation>().CountAsync()));
    }

    // LID sem par conhecido não é apagado nem inventado — fica e é reportado.
    [Fact]
    public async Task LidConsolidation_LeavesUnknownLidsAlone()
    {
        await SeedAsync(bothInPool: false);

        await SeedAsync(db =>
        {
            db.Add(new Contact
            {
                Id = Guid.NewGuid(), RemoteJid = "999999@lid",
                PushName = "Desconhecido", CreatedAt = DateTime.UtcNow,
            });
            return Task.CompletedTask;
        });

        var applied = await (await Client.PostAsync("/api/v1/contacts/lid-consolidation", null))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, applied.GetProperty("unresolved").GetInt32());
        Assert.Equal(1, await InDbAsync(db => db.Set<Contact>().CountAsync(c => c.RemoteJid == "999999@lid")));
    }

    // O ack de uma mensagem do aquecimento não acha `Message` nenhuma (ela nunca
    // entrou), mas atualiza o turno — é dele que sai a taxa de entrega do pool,
    // que alimenta o kill switch.
    [Fact]
    public async Task WarmupAck_UpdatesTheTurnInsteadOfBeingDiscarded()
    {
        await SeedAsync(bothInPool: true);

        var peerA = await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking()
            .FirstAsync(p => p.WhatsappNumberId == _numberA));
        var peerB = await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking()
            .FirstAsync(p => p.WhatsappNumberId == _numberB));
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        await SeedAsync(db =>
        {
            db.Add(new WarmupConversation
            {
                Id = conversationId, PeerAId = peerA.Id, PeerBId = peerB.Id,
                Theme = "combinar o almoço", Status = WarmupConversationStatus.Running,
                CreatedAt = DateTime.UtcNow,
            });
            db.Add(new WarmupTurn
            {
                Id = turnId, ConversationId = conversationId, Sequence = 1, FromPeerId = peerA.Id,
                Text = "bora almoçar?", ScheduledAt = DateTime.UtcNow, SentAt = DateTime.UtcNow,
                WaMessageId = "ACK-1",
            });
            return Task.CompletedTask;
        });

        await PostWebhookAsync($$"""
            {
              "event": "messages.update", "instance": "{{InstanceA}}",
              "data": { "keyId": "ACK-1", "status": "DELIVERY_ACK" }
            }
            """);
        await ProcessAsync();

        var turn = await InDbAsync(db => db.Set<WarmupTurn>().AsNoTracking().SingleAsync(t => t.Id == turnId));
        Assert.NotNull(turn.DeliveredAt);
    }
}
