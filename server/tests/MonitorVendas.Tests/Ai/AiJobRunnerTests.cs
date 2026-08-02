using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

// O runner é quem gasta dinheiro: o que ele decide analisar, pular ou marcar como
// falho aparece na conta do provedor. Cada caminho aqui é uma cobrança a mais ou
// a menos.
public class AiJobRunnerTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000ab");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000ab");
    private static readonly Guid FirstConversation = Guid.Parse("c0117e00-0000-0000-0000-0000000000a1");
    private static readonly Guid SecondConversation = Guid.Parse("c0117e00-0000-0000-0000-0000000000a2");

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

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900002222",
                InstanceName = "mv-runner",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            foreach (var (id, index) in new[] { (FirstConversation, 1), (SecondConversation, 2) })
            {
                var contactId = Guid.NewGuid();
                var start = PeriodStart.AddDays(index);

                db.Add(new Contact { Id = contactId, RemoteJid = $"551197777600{index}@s.whatsapp.net", PushName = $"Cliente {index}", CreatedAt = start });
                db.Add(new Conversation
                {
                    Id = id,
                    WhatsappNumberId = NumberId,
                    SellerId = SellerId,
                    ContactId = contactId,
                    StartedByContact = true,
                    StartedAt = start,
                    LastMessageAt = start.AddMinutes(30),
                });
                db.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = id,
                    WhatsappNumberId = NumberId,
                    SellerId = SellerId,
                    WaMessageId = $"m-{index}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "quanto custa?",
                    Timestamp = start,
                });
            }

            return Task.CompletedTask;
        });
    }

    private async Task RunPendingAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();
    }

    private Task<AiJob> JobAsync() =>
        InDbAsync(db => db.Set<AiJob>().AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync());

    // Conversa marcada na tela ganha do período: o usuário escolheu aquelas, e
    // analisar as outras junto seria gastar o que ele não pediu.
    [Fact]
    public async Task Analysis_WithAnExplicitSelection_OnlyReadsWhatWasMarked()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        var response = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", new
        {
            from = PeriodStart,
            to = PeriodEnd,
            conversationIds = new[] { FirstConversation },
        });
        response.EnsureSuccessStatusCode();

        await RunPendingAsync();

        var job = await JobAsync();
        Assert.Equal(AiJobStatus.Completed, job.Status);
        Assert.Equal(1, job.Total);

        var analysis = await InDbAsync(db => db.Set<ConversationAiAnalysis>().AsNoTracking().SingleAsync());
        Assert.Equal(FirstConversation, analysis.ConversationId);
    }

    // Vendedor cuja síntese falhou entra como "pulado", e o job termina bem: uma
    // cota estourada em um vendedor não pode marcar a rodada inteira como falha.
    [Fact]
    public async Task Synthesis_WhenTheProviderRefuses_CountsAsSkipped()
    {
        await SeedAsync();
        FakeAi.Always(Answer);
        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", new { from = PeriodStart, to = PeriodEnd });
        await RunPendingAsync();

        FakeAi.Reset();
        FakeAi.EnqueueStatus(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota"}}""");
        var response = await Client.PostAsJsonAsync("/api/v1/ai/syntheses/run", new { from = PeriodStart, to = PeriodEnd });
        response.EnsureSuccessStatusCode();

        await RunPendingAsync();

        var job = await JobAsync();
        Assert.Equal(AiJobKind.Synthesize, job.Kind);
        Assert.Equal(AiJobStatus.Completed, job.Status);
        Assert.Equal(1, job.Skipped);
        Assert.Equal(0, job.Processed);
        Assert.Null(job.Active);
    }

    // O serviço em background é quem roda a fila em produção: na largada ele
    // devolve a vaga de um job preso e depois processa o que está pendente.
    [Fact]
    public async Task TheBackgroundLoop_ReleasesStuckJobsAndRunsThePendingOnes()
    {
        await SeedAsync();
        FakeAi.Always(Answer);

        var stuckId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            // Job "rodando" de um processo que morreu: só existe um runner.
            db.Add(new AiJob
            {
                Id = stuckId,
                Kind = AiJobKind.Analyze,
                Status = AiJobStatus.Running,
                Active = true,
                FiltersJson = "{}",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
            });

            return Task.CompletedTask;
        });

        var service = new AiJobBackgroundService(
            Factory.Services.GetRequiredService<IAiJobRunner>(),
            Options.Create(new AiJobOptions { Enabled = true, IntervalSeconds = 1 }),
            Factory.Services.GetRequiredService<ILogger<AiJobBackgroundService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.StartAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested
                && await InDbAsync(db => db.Set<AiJob>().CountAsync(j => j.Id == stuckId && j.Status == AiJobStatus.Failed)) == 0)
                await Task.Delay(50, cts.Token);

            // Com a vaga livre, um pedido novo entra e o laço o executa sozinho.
            var response = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", new { from = PeriodStart, to = PeriodEnd });
            response.EnsureSuccessStatusCode();

            while (!cts.IsCancellationRequested
                && await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()) < 2)
                await Task.Delay(50, cts.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Set<AiJob>().CountAsync(j => j.Active == true)));
    }
}
