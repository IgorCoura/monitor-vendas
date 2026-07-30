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

public class AiAnalysisEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000000a");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000000a");

    private static string Answer(string status, string? loss = null, bool recontact = true) =>
        JsonSerializer.Serialize(new
        {
            status,
            confidence = 0.9,
            evidence = "achei caro",
            lossReason = loss,
            askedForSale = false,
            ignoredBuyingSignal = true,
            objections = new[] { "preço" },
            shouldRecontact = recontact,
            recontactReason = "sumiu",
            suggestedMessage = "consigo melhorar",
            interest = "kit",
            summary = $"conversa classificada como {status}",
            conductAlert = (string?)null,
        });

    private string Query => $"from={PeriodStart:O}&to={PeriodEnd:O}";

    private async Task SeedAsync(int conversations = 2)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = "mv-a",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            for (var i = 0; i < conversations; i++)
            {
                var contactId = Guid.NewGuid();
                var conversationId = Guid.NewGuid();
                var start = PeriodStart.AddDays(1).AddHours(i);

                db.Add(new Contact { Id = contactId, RemoteJid = $"55119777760{i:D2}@s.whatsapp.net", PushName = $"Cliente {i}", CreatedAt = start });
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
                    WaMessageId = $"m-{i}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "quanto custa?",
                    Timestamp = start,
                });
            }

            return Task.CompletedTask;
        });
    }

    private async Task<AiJobDto> RunJobAsync(string path)
    {
        var created = await (await Client.PostAsJsonAsync($"/api/v1/{path}", new { from = PeriodStart, to = PeriodEnd }))
            .Content.ReadFromJsonAsync<AiJobDto>();

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();

        return (await Client.GetFromJsonAsync<AiJobDto>($"/api/v1/ai/jobs/{created!.Id}"))!;
    }

    // O botão "analisar conversas" cria um job que lê as conversas do filtro e a
    // tela passa a listá-las com o status que a IA deu.
    [Fact]
    public async Task Analyze_ThenList_ShowsTheReadings()
    {
        await SeedAsync();
        FakeAi.Always(Answer("lost", "preco"));

        var job = await RunJobAsync("ai/analyses/run");

        Assert.Equal("Completed", job.Status);
        Assert.Equal(2, job.Processed);
        Assert.True(job.CostBrl > 0);

        var page = await Client.GetFromJsonAsync<AiAnalysisPageDto>($"/api/v1/ai/analyses?{Query}");
        Assert.Equal(2, page!.Total);
        Assert.All(page.Items, item =>
        {
            Assert.Equal("Clientes perdidos", item.AiStatus);
            Assert.Equal("Preço", item.LossReason);
            Assert.Equal("Ana", item.SellerName);
            Assert.True(item.Divergent);
        });
    }

    // Os filtros da tela recortam a lista — aqui, só o que a IA marcou como perdido.
    [Fact]
    public async Task List_FiltersByStatus()
    {
        await SeedAsync();
        FakeAi.Enqueue(Answer("lost", "preco"));
        FakeAi.Enqueue(Answer("sale"));
        await RunJobAsync("ai/analyses/run");

        var perdidas = await Client.GetFromJsonAsync<AiAnalysisPageDto>($"/api/v1/ai/analyses?{Query}&status=lost");
        var vendas = await Client.GetFromJsonAsync<AiAnalysisPageDto>($"/api/v1/ai/analyses?{Query}&status=sale");

        Assert.Equal(1, perdidas!.Total);
        Assert.Equal(1, vendas!.Total);
        Assert.Equal("Vendas", vendas.Items[0].AiStatus);
    }

    // Reanalisar cria uma versão nova: a tela mostra a leitura corrente e quantas
    // versões existem, sem perder o histórico.
    [Fact]
    public async Task Analyze_Twice_KeepsVersionCount()
    {
        await SeedAsync(1);
        FakeAi.Always(Answer("lost", "preco"));

        await RunJobAsync("ai/analyses/run");
        await RunJobAsync("ai/analyses/run");

        var page = await Client.GetFromJsonAsync<AiAnalysisPageDto>($"/api/v1/ai/analyses?{Query}");
        var item = Assert.Single(page!.Items);
        Assert.Equal(2, item.Versions);
        Assert.Equal(2, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync(a => a.IsCurrent)));
    }

    // O botão "refazer síntese" gera a síntese do vendedor a partir das leituras
    // correntes, e ela aparece na tela.
    [Fact]
    public async Task Synthesize_ProducesASynthesisForTheSeller()
    {
        await SeedAsync();
        FakeAi.Always(Answer("lost", "preco"));
        await RunJobAsync("ai/analyses/run");

        FakeAi.Always(JsonSerializer.Serialize(new
        {
            overview = "amostra pequena",
            strengths = new[] { "responde rápido" },
            improvements = new[] { "não pede a venda" },
            dominantLossPattern = "preço",
            trainingSuggestion = "treinar fechamento",
        }));

        var job = await RunJobAsync("ai/syntheses/run");
        Assert.Equal("Completed", job.Status);
        Assert.Equal(1, job.Processed);

        var syntheses = await Client.GetFromJsonAsync<List<AiSynthesisDto>>("/api/v1/ai/syntheses");
        var synthesis = Assert.Single(syntheses!);
        Assert.Equal("Ana", synthesis.SellerName);
        Assert.Equal("amostra pequena", synthesis.Overview);
        Assert.Equal("responde rápido", Assert.Single(synthesis.Strengths));
        Assert.False(synthesis.Stale);
    }

    // Reanalisar depois de sintetizar deixa a síntese desatualizada, e a tela avisa
    // — senão o usuário leria um parecer que descreve leituras que já mudaram.
    [Fact]
    public async Task Synthesis_AfterReanalysis_IsMarkedStale()
    {
        await SeedAsync(1);
        FakeAi.Always(Answer("lost", "preco"));
        await RunJobAsync("ai/analyses/run");

        FakeAi.Always(JsonSerializer.Serialize(new { overview = "ok", strengths = new[] { "a" }, improvements = new[] { "b" } }));
        await RunJobAsync("ai/syntheses/run");

        FakeAi.Always(Answer("sale"));
        await RunJobAsync("ai/analyses/run");

        var syntheses = await Client.GetFromJsonAsync<List<AiSynthesisDto>>("/api/v1/ai/syntheses");
        Assert.True(Assert.Single(syntheses!).Stale);
    }
}
