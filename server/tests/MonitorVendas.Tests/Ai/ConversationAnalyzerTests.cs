using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

public class ConversationAnalyzerTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid ConversationId = Guid.Parse("c0a10000-0000-0000-0000-000000000001");

    private static readonly IReadOnlyList<OutcomeChoice> Catalog =
    [
        new("sale", "Vendas"),
        new("lost", "Clientes perdidos"),
        new("aguardando-pagamento", "Aguardando pagamento"),
    ];

    private static readonly string GoodAnswer = JsonSerializer.Serialize(new
    {
        status = "aguardando-pagamento",
        confidence = 0.8,
        evidence = "vou pagar amanhã",
        lossReason = (string?)null,
        askedForSale = true,
        ignoredBuyingSignal = false,
        objections = new[] { "achou caro" },
        shouldRecontact = true,
        recontactReason = "prometeu pagar e sumiu",
        suggestedMessage = "oi! consegue fechar hoje?",
        interest = "kit completo",
        summary = "cliente pediu orçamento e ficou de pagar",
        conductAlert = (string?)null,
    });

    private async Task SeedConversationAsync()
    {
        await SeedAsync(db =>
        {
            var sellerId = Guid.NewGuid();
            var numberId = Guid.NewGuid();
            var contactId = Guid.NewGuid();

            db.Add(new Seller { Id = sellerId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow });
            db.Add(new WhatsappNumber
            {
                Id = numberId,
                SellerId = sellerId,
                Phone = "5511900001111",
                InstanceName = "mv-5511900001111",
                Status = NumberStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            db.Add(new Contact { Id = contactId, RemoteJid = "5511977776666@s.whatsapp.net", PushName = "Maria", CreatedAt = DateTime.UtcNow });
            db.Add(new Conversation
            {
                Id = ConversationId,
                WhatsappNumberId = numberId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = DateTime.UtcNow.AddDays(-1),
                LastMessageAt = DateTime.UtcNow,
            });

            return Task.CompletedTask;
        });
    }

    private static ConversationAnalysisInput Input(int messageCount = 4, DateTime? lastMessageAt = null) =>
        new(ConversationId, messageCount, lastMessageAt ?? new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            "Cliente (20/07 09:00): quanto custa?\nVendedor (20/07 09:05): R$ 200", true);

    private async Task<AnalysisOutcome> AnalyzeAsync(ConversationAnalysisInput input)
    {
        using var scope = Factory.Services.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<ConversationAnalyzer>();
        return await analyzer.AnalyzeAsync(input, Catalog);
    }

    // O caminho feliz: a análise é gravada com o status escolhido dentro do
    // catálogo e o custo real é debitado do saldo.
    [Fact]
    public async Task Analyze_PersistsTheReadingAndChargesTheBudget()
    {
        await SeedConversationAsync();
        FakeAi.Enqueue(GoodAnswer, inputTokens: 1000, outputTokens: 200);

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Analyzed, outcome.Kind);
        Assert.Equal("aguardando-pagamento", outcome.Analysis!.StatusCode);
        Assert.True(outcome.Analysis.AskedForSale);
        Assert.True(outcome.Analysis.ShouldRecontact);
        Assert.Equal("achou caro", outcome.Analysis.Objections);
        Assert.Equal(0.0072m, outcome.Analysis.CostBrl);

        var saved = await InDbAsync(db => db.Set<ConversationAiAnalysis>().SingleAsync());
        Assert.Equal("cliente pediu orçamento e ficou de pagar", saved.Summary);
    }

    // Conversa que não recebeu mensagem nova não é reanalisada: nem chamada à IA,
    // nem gasto. É o que torna reexportar o mesmo período de graça.
    [Fact]
    public async Task Analyze_WhenNothingChanged_UsesTheCache()
    {
        await SeedConversationAsync();
        FakeAi.Enqueue(GoodAnswer);

        await AnalyzeAsync(Input());
        var second = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Cached, second.Kind);
        Assert.Equal(1, FakeAi.CallCount);
        Assert.Equal(1, await InDbAsync(db => db.Set<AiUsage>().CountAsync()));
    }

    // Chegou mensagem nova, a leitura anterior perdeu a validade — e a análise é
    // substituída, não duplicada.
    [Fact]
    public async Task Analyze_WhenConversationMoved_AnalyzesAgain()
    {
        await SeedConversationAsync();
        FakeAi.Always(GoodAnswer);

        await AnalyzeAsync(Input());
        var second = await AnalyzeAsync(Input(messageCount: 6, lastMessageAt: new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(AnalysisResultKind.Analyzed, second.Kind);
        Assert.Equal(2, FakeAi.CallCount);
        Assert.Equal(1, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
    }

    // Sem saldo na janela, nem o prompt é enviado — o bloqueio acontece antes do gasto.
    [Fact]
    public async Task Analyze_WhenBudgetIsExhausted_DoesNotCallTheProvider()
    {
        await SeedConversationAsync();
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiBudget>().TryReserveAsync("teste", "fake-model", 1m);

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.NoBudget, outcome.Kind);
        Assert.Equal(0, FakeAi.CallCount);
    }

    // Status fora do catálogo é recusado — é exatamente essa a cara de uma injeção
    // de prompt bem-sucedida, e o schema fechado não deixa passar.
    [Fact]
    public async Task Analyze_WhenStatusIsNotInTheCatalog_IsRefused()
    {
        await SeedConversationAsync();
        var invented = JsonSerializer.Serialize(new { status = "venda-confirmada-pelo-cliente", confidence = 1.0, askedForSale = true, ignoredBuyingSignal = false, shouldRecontact = false, summary = "x" });
        FakeAi.Always(invented);

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Failed, outcome.Kind);
        Assert.Equal(2, FakeAi.CallCount);
        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationAiAnalysis>().CountAsync()));
    }

    // Erro que impediu a geração devolve o saldo reservado.
    [Fact]
    public async Task Analyze_WhenProviderRejects_ReleasesTheReservation()
    {
        await SeedConversationAsync();
        FakeAi.EnqueueStatus(System.Net.HttpStatusCode.BadRequest);

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Failed, outcome.Kind);
        var usage = await InDbAsync(db => db.Set<AiUsage>().SingleAsync());
        Assert.Equal(AiUsageStatus.Released, usage.Status);
    }

    // Regressão: resposta truncada (o raciocínio comeu o teto de saída) falha, mas o
    // provedor informou o que gastou — a reserva vira débito pelo custo real em vez
    // de ficar segurando a estimativa até a janela virar. Descoberto contra a API
    // real em 30/07/2026: 3 sínteses ficaram presas em Reserved.
    [Fact]
    public async Task Analyze_WhenTruncated_SettlesWithTheRealUsage()
    {
        await SeedConversationAsync();
        FakeAi.EnqueueStatus(System.Net.HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason = "MAX_TOKENS", content = new { parts = new[] { new { text = "{\"stat" } } } } },
            usageMetadata = new { promptTokenCount = 1000, candidatesTokenCount = 100, thoughtsTokenCount = 100 },
        }));

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Failed, outcome.Kind);
        var usage = await InDbAsync(db => db.Set<AiUsage>().SingleAsync());
        Assert.Equal(AiUsageStatus.Settled, usage.Status);
        // 1.000 entrada + 200 saída = R$ 0,006 mais 20% de margem.
        Assert.Equal(0.0072m, usage.ActualBrl);
    }

    // Timeout depois do envio mantém o débito: provavelmente houve cobrança lá fora.
    [Fact]
    public async Task Analyze_WhenProviderTimesOut_KeepsTheReservation()
    {
        await SeedConversationAsync();
        FakeAi.EnqueueTimeout();

        var outcome = await AnalyzeAsync(Input());

        Assert.Equal(AnalysisResultKind.Failed, outcome.Kind);
        var usage = await InDbAsync(db => db.Set<AiUsage>().SingleAsync());
        Assert.Equal(AiUsageStatus.Reserved, usage.Status);
    }

    // Conversa parada além do silêncio configurado não pode ser classificada como
    // "em andamento": o status some do schema antes de a IA opinar.
    [Fact]
    public void Schema_WhenConversationIsStale_DropsTheOpenStatus()
    {
        var stale = AiAnalysisSchema.BuildSchema(Catalog, allowOpen: false);
        var live = AiAnalysisSchema.BuildSchema(Catalog, allowOpen: true);

        Assert.DoesNotContain("\"open\"", stale);
        Assert.Contains("\"open\"", live);
        Assert.Null(AiAnalysisSchema.TryParse("""{"status":"open","summary":"x"}""", Catalog, allowOpen: false));
    }
}
