using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

public class AiJobConcurrencyTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000000c");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000000c");

    private static readonly string Answer = JsonSerializer.Serialize(new
    {
        status = "lost",
        confidence = 0.9,
        evidence = "achei caro",
        lossReason = "preco",
        askedForSale = false,
        ignoredBuyingSignal = false,
        objections = new[] { "preço" },
        shouldRecontact = false,
        recontactReason = (string?)null,
        suggestedMessage = (string?)null,
        interest = "kit",
        summary = "cliente achou caro",
        conductAlert = (string?)null,
    });

    private object Body => new { from = PeriodStart, to = PeriodEnd };

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = "mv-c",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var start = PeriodStart.AddDays(1);

            db.Add(new Contact { Id = contactId, RemoteJid = "5511977776000@s.whatsapp.net", PushName = "Cliente", CreatedAt = start });
            db.Add(new Conversation
            {
                Id = conversationId,
                WhatsappNumberId = NumberId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = start,
                LastMessageAt = start.AddMinutes(30),
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = NumberId,
                WaMessageId = "m-1",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "quanto custa?",
                Timestamp = start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task RunPendingAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();
    }

    // Uma rodada por vez: com um job ativo, o segundo pedido é recusado — análise
    // e síntese disputam a mesma vaga porque disputam a mesma cota do provedor.
    [Fact]
    public async Task SecondRun_WhileOneIsActive_Returns409()
    {
        await SeedAsync();

        var first = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        first.EnsureSuccessStatusCode();

        var second = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        var otherKind = await Client.PostAsJsonAsync("/api/v1/ai/syntheses/run", Body);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherKind.StatusCode);
        Assert.Equal(1, await InDbAsync(db => db.Set<AiJob>().CountAsync()));
    }

    // Terminada a rodada, a flag cai e a vaga volta a ficar livre.
    [Fact]
    public async Task AfterTheJobFinishes_TheSlotIsFree()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();

        var status = await Client.GetFromJsonAsync<AiStatusDto>("/api/v1/ai/status");
        Assert.Null(status!.Running);
        Assert.Equal("Completed", status.LastAnalysis!.Status);
        Assert.NotNull(status.LastAnalysis.CompletedAt);
        // A síntese nunca rodou: a data dela fica vazia, separada da análise.
        Assert.Null(status.LastSynthesis);

        var again = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        again.EnsureSuccessStatusCode();
    }

    // Job que falhou também libera a vaga: flag de pé depois de um erro travaria
    // os botões até alguém mexer no banco.
    [Fact]
    public async Task FailedJob_ReleasesTheSlot()
    {
        await SeedAsync();
        FakeAi.Always("isto não é json");

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();

        var status = await Client.GetFromJsonAsync<AiStatusDto>("/api/v1/ai/status");
        Assert.Null(status!.Running);
        Assert.False(await InDbAsync(db => db.Set<AiJob>().AnyAsync(j => j.Active == true)));
    }

    // Enquanto roda, a tela vê a rodada em andamento e sabe qual das duas é —
    // é o que trava os botões mesmo depois de recarregar a página.
    [Fact]
    public async Task Status_WhileRunning_ReportsTheActiveJob()
    {
        await SeedAsync();

        await Client.PostAsJsonAsync("/api/v1/ai/syntheses/run", Body);

        var status = await Client.GetFromJsonAsync<AiStatusDto>("/api/v1/ai/status");
        Assert.NotNull(status!.Running);
        Assert.Equal("Synthesize", status.Running.Kind);
        Assert.Equal("Pending", status.Running.Status);
    }

    // Job que ficou "rodando" é de um processo que morreu no meio: na volta ele é
    // encerrado e a vaga liberada, senão os botões nunca voltariam.
    [Fact]
    public async Task StuckJob_IsReleasedOnStartup()
    {
        await SeedAsync();
        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await SeedAsync(db =>
        {
            db.Set<AiJob>().Single().Status = AiJobStatus.Running;
            return Task.CompletedTask;
        });

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ReleaseStuckJobsAsync();

        var status = await Client.GetFromJsonAsync<AiStatusDto>("/api/v1/ai/status");
        Assert.Null(status!.Running);
        Assert.Equal("Failed", status.LastAnalysis!.Status);
        Assert.Equal("A rodada foi interrompida antes de terminar.", status.LastAnalysis.Error);
    }

    // Sem saldo para a rodada inteira, o pedido é recusado na hora e nem vira job:
    // esperar para receber o erro depois só faria o usuário perder tempo.
    [Fact]
    public async Task Run_WithoutBudget_Returns422AndCreatesNoJob()
    {
        await SeedAsync();
        // O saldo da janela é R$ 1,00 na factory; reservar tudo zera o disponível.
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiBudget>().TryReserveAsync("teste", "fake-model", 1m);

        var response = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Análise não realizada por falta de saldo.", body.GetProperty("error").GetString());
        Assert.Equal(0, await InDbAsync(db => db.Set<AiJob>().CountAsync()));
        Assert.Equal(0, FakeAi.CallCount);
    }

    // O saldo pode acabar entre o clique e a vez do job: a segunda barreira está
    // no runner, e ele desiste antes de mandar o primeiro token.
    [Fact]
    public async Task Job_WhenBudgetIsGoneBeforeItRuns_FailsWithoutSpending()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiBudget>().TryReserveAsync("teste", "fake-model", 1m);

        await RunPendingAsync();

        var status = await Client.GetFromJsonAsync<AiStatusDto>("/api/v1/ai/status");
        Assert.Equal("Failed", status!.LastAnalysis!.Status);
        Assert.Equal("Análise não realizada por falta de saldo.", status.LastAnalysis.Error);
        Assert.Equal(0, FakeAi.CallCount);
        Assert.Null(status.Running);
    }

    // O padrão é refazer só o que mudou: conversa com leitura válida não é
    // reanalisada, a estimativa zera e nenhuma chamada é feita.
    [Fact]
    public async Task Analysis_ByDefault_SkipsConversationsThatDidNotChange()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        var before = await EstimateAsync();
        Assert.Equal(1, before.Conversations);
        Assert.Equal(0, before.Cached);
        Assert.True(before.EstimatedBrl > 0);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();
        var callsAfterFirst = FakeAi.CallCount;

        var after = await EstimateAsync();
        Assert.Equal(1, after.Cached);
        Assert.Equal(0m, after.EstimatedBrl);

        var response = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        response.EnsureSuccessStatusCode();
        await RunPendingAsync();

        // Nada mudou na conversa: nenhuma chamada nova e nenhuma versão nova.
        Assert.Equal(callsAfterFirst, FakeAi.CallCount);
        Assert.Equal(1, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
    }

    // Conversa com mensagem nova volta a custar: é exatamente o que a rodada
    // padrão precisa reanalisar.
    [Fact]
    public async Task Analysis_ReanalyzesWhatChanged()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();
        var callsAfterFirst = FakeAi.CallCount;

        await SeedAsync(db =>
        {
            var conversation = db.Set<Conversation>().Single();
            conversation.LastMessageAt = conversation.LastMessageAt.AddMinutes(5);
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                WhatsappNumberId = NumberId,
                WaMessageId = "m-2",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "ainda está disponível?",
                Timestamp = conversation.LastMessageAt,
            });
            return Task.CompletedTask;
        });

        var estimate = await EstimateAsync();
        Assert.Equal(0, estimate.Cached);
        Assert.True(estimate.EstimatedBrl > 0);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();

        Assert.Equal(callsAfterFirst + 1, FakeAi.CallCount);
        // A leitura anterior vira histórico; só a nova é a corrente.
        Assert.Equal(2, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync(a => a.IsCurrent)));
    }

    // `force: true` continua existindo na API, sem botão na tela: relê tudo e
    // cobra tudo, para quando o prompt ou o modelo mudam.
    [Fact]
    public async Task Analysis_WithForce_ReanalyzesEverything()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", Body);
        await RunPendingAsync();
        var callsAfterFirst = FakeAi.CallCount;

        var estimate = await EstimateAsync(force: true);
        Assert.Equal(1, estimate.Cached);
        Assert.True(estimate.EstimatedBrl > 0);

        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", new
        {
            from = PeriodStart,
            to = PeriodEnd,
            force = true,
        });
        await RunPendingAsync();

        Assert.Equal(callsAfterFirst + 1, FakeAi.CallCount);
        Assert.Equal(2, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
    }

    private async Task<AiEstimate> EstimateAsync(bool? force = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/ai/estimate", new
        {
            kind = "Analyze",
            from = PeriodStart,
            to = PeriodEnd,
            force,
        });

        return (await response.Content.ReadFromJsonAsync<AiEstimate>())!;
    }
}
