using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Warmup;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Warmup;

// Agendador e executor do aquecimento: quem fala com quem, quando sai, e o que
// faz o pool inteiro parar.
public class WarmupSchedulerTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string InstanceA = "mv-5511900001111";
    private const string PhoneA = "5511900001111";
    private const string PhoneB = "5511900002222";

    private Guid _numberA;
    private Guid _numberB;
    private Guid _peerA;
    private Guid _peerB;

    // Diálogo válido: mensagens curtas, os dois lados falam, sem link, telefone
    // nem cara de anúncio — passa pelo WarmupContentValidator.
    private const string Dialogue = """
        {"turnos":[
          {"de":"A","texto":"bom dia, tudo certo por ai?"},
          {"de":"B","texto":"tudo, e vc?"},
          {"de":"A","texto":"o aluno mandou o tcc ontem"},
          {"de":"B","texto":"ja viu se ta completo?"},
          {"de":"A","texto":"ainda nao, vou olhar agora"},
          {"de":"B","texto":"beleza, me avisa"},
          {"de":"A","texto":"fechou"}
        ]}
        """;

    private IWarmupScheduler Scheduler => Factory.Services.GetRequiredService<IWarmupScheduler>();

    private IWarmupExecutor Executor => Factory.Services.GetRequiredService<IWarmupExecutor>();

    private async Task<Guid> AddNumberAsync(Guid sellerId, string phone)
    {
        var number = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{sellerId}/numbers", new { phone }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return number.GetProperty("number").GetProperty("id").GetGuid();
    }

    private async Task SeedPoolAsync(bool enabled = true, bool withLink = true)
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Post, "/settings/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");
        FakeAi.Always(Dialogue);

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sellerId = seller.GetProperty("id").GetGuid();

        _numberA = await AddNumberAsync(sellerId, PhoneA);
        _numberB = await AddNumberAsync(sellerId, PhoneB);

        foreach (var instance in new[] { InstanceA, "mv-5511900002222" })
            await PostWebhookAsync($$"""
                { "event": "connection.update", "instance": "{{instance}}",
                  "data": { "state": "open", "statusReason": 200 }, "date_time": "2026-06-30T12:00:00Z" }
                """);

        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();

        _peerA = Guid.NewGuid();
        _peerB = Guid.NewGuid();
        // PeerAId é sempre o menor Guid — a mesma normalização do WarmupGraph.
        var (first, second) = _peerA.CompareTo(_peerB) < 0 ? (_peerA, _peerB) : (_peerB, _peerA);

        await SeedAsync(db =>
        {
            db.Add(new WarmupPeer
            {
                Id = _peerA, WhatsappNumberId = _numberA,
                Persona = WarmupPersona.Seco, JoinedAt = DateTime.UtcNow.AddDays(-60),
            });
            db.Add(new WarmupPeer
            {
                Id = _peerB, WhatsappNumberId = _numberB,
                Persona = WarmupPersona.Falante, JoinedAt = DateTime.UtcNow.AddDays(-60),
            });

            if (withLink)
            {
                db.Add(new WarmupLink
                {
                    Id = Guid.NewGuid(), PeerAId = first, PeerBId = second,
                    Kind = WarmupLinkKind.Core, ConversationsPerWeek = 5,
                    CreatedAt = DateTime.UtcNow.AddDays(-60),
                });
            }

            db.Add(new WarmupSettings
            {
                Id = WarmupSettings.SingletonId, Enabled = enabled, UpdatedAt = DateTime.UtcNow,
            });

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

    // Cria uma conversa já vencida, pronta para o executor pegar.
    private async Task<Guid> SeedDueConversationAsync(int turns = 2)
    {
        var conversationId = Guid.NewGuid();

        await SeedAsync(db =>
        {
            db.Add(new WarmupConversation
            {
                Id = conversationId, PeerAId = _peerA, PeerBId = _peerB,
                Theme = "combinar o almoço", Status = WarmupConversationStatus.Scheduled,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            });

            for (var i = 0; i < turns; i++)
            {
                db.Add(new WarmupTurn
                {
                    Id = Guid.NewGuid(), ConversationId = conversationId, Sequence = i + 1,
                    FromPeerId = i % 2 == 0 ? _peerA : _peerB,
                    Text = i % 2 == 0 ? "bora almoçar?" : "bora, meio dia",
                    ScheduledAt = DateTime.UtcNow.AddMinutes(-5),
                });
            }

            return Task.CompletedTask;
        });

        return conversationId;
    }

    private Task<WarmupSettings> SettingsAsync() =>
        InDbAsync(db => db.Set<WarmupSettings>().AsNoTracking().SingleAsync());

    // Pool ligado, dois números com aresta: o agendador cria uma conversa com os
    // turnos já com hora marcada.
    [Fact]
    public async Task Scheduler_CreatesAConversationWithScheduledTurns()
    {
        await SeedPoolAsync();

        await Scheduler.RunOnceAsync();

        var conversation = await InDbAsync(db => db.Set<WarmupConversation>().AsNoTracking().SingleOrDefaultAsync());
        Assert.NotNull(conversation);
        Assert.Equal(WarmupConversationStatus.Scheduled, conversation.Status);

        var turns = await InDbAsync(db => db.Set<WarmupTurn>().AsNoTracking()
            .Where(t => t.ConversationId == conversation.Id).OrderBy(t => t.Sequence).ToListAsync());

        Assert.True(turns.Count >= 2);
        Assert.All(turns, t => Assert.Null(t.SentAt));
        // Os turnos são escalonados: sair tudo no mesmo instante é rajada.
        Assert.True(turns[1].ScheduledAt > turns[0].ScheduledAt);
        // E os dois lados falam.
        Assert.Equal(2, turns.Select(t => t.FromPeerId).Distinct().Count());
    }

    // Interruptor desligado: nada é agendado, nem uma chamada de IA é gasta.
    [Fact]
    public async Task Scheduler_DoesNothingWhileTheSwitchIsOff()
    {
        await SeedPoolAsync(enabled: false);

        await Scheduler.RunOnceAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
        Assert.Equal(0, FakeAi.CallCount);
    }

    // Kill switch armado: mesmo com o interruptor ligado, nada sai até alguém
    // religar à mão.
    [Fact]
    public async Task Scheduler_DoesNothingWhileHalted()
    {
        await SeedPoolAsync();
        await SeedAsync(async db =>
        {
            await db.Set<WarmupSettings>().ExecuteUpdateAsync(s => s
                .SetProperty(x => x.HaltedAt, DateTime.UtcNow)
                .SetProperty(x => x.HaltReason, "teste"));
        });

        await Scheduler.RunOnceAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
    }

    // Número desconectado sai do pool sozinho: sem os dois lados elegíveis não há
    // conversa a agendar.
    [Fact]
    public async Task Scheduler_SkipsNumbersThatAreNotActive()
    {
        await SeedPoolAsync();
        await SeedAsync(async db =>
        {
            await db.Set<WhatsappNumber>().Where(n => n.Id == _numberB)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NumberStatus.Disconnected));
        });

        await Scheduler.RunOnceAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
    }

    // Número com envio pausado pelo WhatsApp (463) também fica de fora — insistir
    // ali é o oposto do que o aquecimento quer.
    [Fact]
    public async Task Scheduler_SkipsNumbersWithSendingPaused()
    {
        await SeedPoolAsync();
        await SeedAsync(async db =>
        {
            await db.Set<WhatsappNumber>().Where(n => n.Id == _numberB)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.SendingPausedUntil, DateTime.UtcNow.AddHours(6)));
        });

        await Scheduler.RunOnceAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
    }

    // Uma conversa por vez por número: duas em paralelo, no mesmo minuto, é
    // padrão de robô.
    [Fact]
    public async Task Scheduler_DoesNotStartASecondConversationForABusyPeer()
    {
        await SeedPoolAsync();
        await SeedDueConversationAsync();

        await Scheduler.RunOnceAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
    }

    // O executor manda os turnos vencidos pela instância do remetente, para o
    // telefone do outro lado, e grava o id da mensagem.
    [Fact]
    public async Task Executor_SendsDueTurnsThroughTheRightInstance()
    {
        await SeedPoolAsync();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"WA-1"}}""");
        await SeedDueConversationAsync(turns: 1);

        var sent = await Executor.ProcessPendingAsync();

        Assert.Equal(1, sent);

        var turn = await InDbAsync(db => db.Set<WarmupTurn>().AsNoTracking().SingleAsync());
        Assert.NotNull(turn.SentAt);
        Assert.Equal("WA-1", turn.WaMessageId);

        var request = FakeEvolution.Requests.Last(r => r.Path.StartsWith("/message/sendText/"));
        Assert.Equal($"/message/sendText/{InstanceA}", request.Path);
        Assert.Contains(PhoneB, request.Body);
    }

    // Terminados todos os turnos, a conversa fecha e o chat é arquivado nos DOIS
    // lados — é isso que impede o celular do vendedor de encher de conversa de
    // colega.
    [Fact]
    public async Task Executor_ArchivesBothSidesWhenTheConversationEnds()
    {
        await SeedPoolAsync();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"WA-1"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/archiveChat/", """{"chatId":"ok"}""");
        var conversationId = await SeedDueConversationAsync(turns: 2);

        await Executor.ProcessPendingAsync();

        var conversation = await InDbAsync(db => db.Set<WarmupConversation>().AsNoTracking()
            .SingleAsync(c => c.Id == conversationId));

        Assert.Equal(WarmupConversationStatus.Completed, conversation.Status);
        Assert.True(conversation.ArchivedA);
        Assert.True(conversation.ArchivedB);

        var archives = FakeEvolution.Requests.Where(r => r.Path.StartsWith("/chat/archiveChat/")).ToList();
        Assert.Equal(2, archives.Count);
    }

    // 463 durante o aquecimento é o sinal mais forte que existe: a conta avisou.
    // Para o POOL INTEIRO, não só o número afetado — se o padrão foi detectado,
    // ele foi detectado no padrão.
    [Fact]
    public async Task Executor_HaltsTheWholePoolWhenWhatsappRestrictsTheSend()
    {
        await SeedPoolAsync();
        FakeEvolution.When(
            HttpMethod.Post, "/message/sendText/",
            """{"status":463,"message":"reachout timelock"}""", HttpStatusCode.BadRequest);
        await SeedDueConversationAsync(turns: 2);

        await Executor.ProcessPendingAsync();

        var settings = await SettingsAsync();
        Assert.NotNull(settings.HaltedAt);
        Assert.Contains("restringiu", settings.HaltReason);

        // E para de verdade: nada mais é enviado na passada seguinte.
        var before = FakeEvolution.Requests.Count(r => r.Path.StartsWith("/message/sendText/"));
        await Executor.ProcessPendingAsync();
        Assert.Equal(before, FakeEvolution.Requests.Count(r => r.Path.StartsWith("/message/sendText/")));
    }

    // Mensagem que não chega é o primeiro sinal de restrição, antes de qualquer
    // erro explícito: taxa de entrega abaixo do mínimo para o pool.
    [Fact]
    public async Task Executor_HaltsWhenTheDeliveryRateCollapses()
    {
        await SeedPoolAsync();
        var conversationId = await SeedDueConversationAsync(turns: 1);

        await SeedAsync(db =>
        {
            // 20 mensagens enviadas há uma hora e nenhuma entregue.
            for (var i = 0; i < 20; i++)
            {
                db.Add(new WarmupTurn
                {
                    Id = Guid.NewGuid(), ConversationId = conversationId, Sequence = 100 + i,
                    FromPeerId = _peerA, Text = "oi",
                    ScheduledAt = DateTime.UtcNow.AddHours(-1),
                    SentAt = DateTime.UtcNow.AddHours(-1),
                });
            }

            return Task.CompletedTask;
        });

        var sent = await Executor.ProcessPendingAsync();

        Assert.Equal(0, sent);
        var settings = await SettingsAsync();
        Assert.NotNull(settings.HaltedAt);
        Assert.Contains("entrega", settings.HaltReason);
    }

    // Amostra pequena não dispara o kill switch: dois envios sem ack ainda não
    // dizem nada, e parar o pool por isso seria alarme falso.
    [Fact]
    public async Task Executor_DoesNotHaltOnASmallSample()
    {
        await SeedPoolAsync();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"WA-1"}}""");
        var conversationId = await SeedDueConversationAsync(turns: 1);

        await SeedAsync(db =>
        {
            for (var i = 0; i < 3; i++)
            {
                db.Add(new WarmupTurn
                {
                    Id = Guid.NewGuid(), ConversationId = conversationId, Sequence = 100 + i,
                    FromPeerId = _peerA, Text = "oi",
                    ScheduledAt = DateTime.UtcNow.AddHours(-1),
                    SentAt = DateTime.UtcNow.AddHours(-1),
                });
            }

            return Task.CompletedTask;
        });

        await Executor.ProcessPendingAsync();

        Assert.Null((await SettingsAsync()).HaltedAt);
    }

    // Envio que falhou por erro de comunicação gasta UMA tentativa por passada —
    // repescar na mesma queimaria as três em sequência, sem intervalo. É o mesmo
    // bug já corrigido no ContactShareSender e no WebhookProcessor.
    [Fact]
    public async Task Executor_SpendsOnlyOneAttemptPerPass()
    {
        await SeedPoolAsync();
        FakeEvolution.When(
            HttpMethod.Post, "/message/sendText/", """{"message":"boom"}""", HttpStatusCode.InternalServerError);
        await SeedDueConversationAsync(turns: 1);

        await Executor.ProcessPendingAsync();

        var turn = await InDbAsync(db => db.Set<WarmupTurn>().AsNoTracking().SingleAsync());
        Assert.Equal(1, turn.Attempts);
        Assert.Null(turn.SentAt);
        Assert.NotNull(turn.Error);
    }

    // O botão de pânico da tela para tudo na hora, sem desligar o interruptor.
    [Fact]
    public async Task HaltEndpoint_StopsThePoolImmediately()
    {
        await SeedPoolAsync();

        var response = await Client.PostAsync("/api/v1/warmup/halt", null);
        response.EnsureSuccessStatusCode();

        var settings = await SettingsAsync();
        Assert.NotNull(settings.HaltedAt);
        Assert.True(settings.Enabled);

        await Scheduler.RunOnceAsync();
        Assert.Equal(0, await InDbAsync(db => db.Set<WarmupConversation>().CountAsync()));
    }

    // Religar pela tela limpa o kill switch: é a decisão manual que ele exige.
    [Fact]
    public async Task TurningItBackOn_ClearsTheKillSwitch()
    {
        await SeedPoolAsync();
        await Client.PostAsync("/api/v1/warmup/halt", null);

        var response = await Client.PutAsJsonAsync("/api/v1/warmup/settings", new { enabled = true });
        response.EnsureSuccessStatusCode();

        var settings = await SettingsAsync();
        Assert.Null(settings.HaltedAt);
        Assert.Null(settings.HaltReason);
    }

    // Entrar e sair do pool pela tela: sair não apaga o histórico, só marca a
    // saída, e voltar reencontra o mesmo peer (e o mesmo círculo).
    [Fact]
    public async Task PeerEndpoints_OptInAndOutWithoutLosingHistory()
    {
        await SeedPoolAsync();

        var removed = await Client.DeleteAsync($"/api/v1/warmup/peers/{_numberB}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.NotNull((await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking().SingleAsync(p => p.Id == _peerB))).LeftAt);

        var back = await Client.PostAsJsonAsync("/api/v1/warmup/peers", new { numberId = _numberB });
        back.EnsureSuccessStatusCode();

        var peer = await InDbAsync(db => db.Set<WarmupPeer>().AsNoTracking().SingleAsync(p => p.WhatsappNumberId == _numberB));
        Assert.Equal(_peerB, peer.Id);
        Assert.Null(peer.LeftAt);
        // A aresta continua lá: a relação existia antes e não some porque o
        // número saiu por uns dias.
        Assert.Equal(1, await InDbAsync(db => db.Set<WarmupLink>().CountAsync()));
    }

    // A tela mostra o pool: quem participa, o círculo de cada um, a meta do dia e
    // as conversas com o texto inteiro.
    [Fact]
    public async Task Overview_ShowsThePoolTheCirclesAndTheConversations()
    {
        await SeedPoolAsync();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"WA-1"}}""");
        await SeedDueConversationAsync(turns: 1);
        await Executor.ProcessPendingAsync();

        var overview = await Client.GetFromJsonAsync<JsonElement>("/api/v1/warmup");

        Assert.True(overview.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, overview.GetProperty("peersInPool").GetInt32());
        Assert.Equal(1, overview.GetProperty("messagesToday").GetInt32());

        var numbers = overview.GetProperty("numbers").EnumerateArray().ToList();
        Assert.Equal(2, numbers.Count);
        Assert.All(numbers, n => Assert.True(n.GetProperty("inPool").GetBoolean()));
        Assert.All(numbers, n => Assert.Equal(1, n.GetProperty("coreCircle").GetInt32()));
        Assert.All(numbers, n => Assert.True(n.GetProperty("goal").GetInt32() >= 20));
        // Pool de 2 números: a meta é capada pela capacidade do grafo.
        Assert.All(numbers, n => Assert.True(n.GetProperty("cappedByGraph").GetBoolean()));

        var conversation = overview.GetProperty("conversations").EnumerateArray().Single();
        Assert.Equal("bora almoçar?", conversation.GetProperty("turns")[0].GetProperty("text").GetString());
    }

    // Número fora do ar aparece na tela com o motivo: "fora do pool" sem
    // explicação vira chamado de suporte.
    [Fact]
    public async Task Overview_ExplainsWhyANumberCannotParticipate()
    {
        await SeedPoolAsync();
        await SeedAsync(async db =>
        {
            await db.Set<WhatsappNumber>().Where(n => n.Id == _numberB)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.BannedUntil, DateTime.UtcNow.AddHours(20)));
        });

        var overview = await Client.GetFromJsonAsync<JsonElement>("/api/v1/warmup");

        var blocked = overview.GetProperty("numbers").EnumerateArray()
            .Single(n => n.GetProperty("numberId").GetGuid() == _numberB);

        Assert.Equal("em cooldown pós-ban", blocked.GetProperty("ineligibleReason").GetString());
    }
}
