using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

// Os filtros da tela /ai são auditoria: é com eles que se acha a conversa que a
// IA leu como perdida e ninguém etiquetou. Filtro que não filtra devolve a lista
// inteira e passa por "não há divergência nenhuma".
public class AiAnalysisFiltersTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);
    private static readonly Guid AnaId = Guid.Parse("5e11e000-0000-0000-0000-0000000000f5");
    private static readonly Guid BrunoId = Guid.Parse("5e11e000-0000-0000-0000-0000000000f6");
    private static readonly Guid AnaNumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000f5");
    private static readonly Guid BrunoNumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000f6");
    private static readonly Guid SoldConversation = Guid.Parse("c0117e00-0000-0000-0000-0000000000f1");
    private static readonly Guid LostConversation = Guid.Parse("c0117e00-0000-0000-0000-0000000000f2");

    private string Query => $"from={PeriodStart:O}&to={PeriodEnd:O}";

    private static string Answer(string status, string? loss, bool recontact) =>
        JsonSerializer.Serialize(new
        {
            status,
            confidence = 0.9,
            evidence = "trecho",
            lossReason = loss,
            askedForSale = false,
            ignoredBuyingSignal = false,
            objections = Array.Empty<string>(),
            shouldRecontact = recontact,
            recontactReason = recontact ? "sumiu" : null,
            suggestedMessage = (string?)null,
            interest = "kit",
            summary = $"conversa {status}",
            conductAlert = (string?)null,
        });

    // Duas conversas do mesmo vendedor: uma etiquetada como venda, outra sem
    // etiqueta nenhuma. É o par que separa divergente de convergente.
    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = AnaId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new Seller { Id = BrunoId, Name = "Bruno", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = AnaNumberId,
                SellerId = AnaId,
                Phone = "5511900005555",
                InstanceName = "mv-filtro-a",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });
            db.Add(new WhatsappNumber
            {
                Id = BrunoNumberId,
                SellerId = BrunoId,
                Phone = "5511900006666",
                InstanceName = "mv-filtro-b",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            var index = 0;
            foreach (var id in new[] { SoldConversation, LostConversation })
            {
                var contactId = Guid.NewGuid();
                var start = PeriodStart.AddDays(1).AddHours(index);

                db.Add(new Contact { Id = contactId, RemoteJid = $"55119777650{index:D2}@s.whatsapp.net", PushName = $"Cliente {index}", CreatedAt = start });
                db.Add(new Conversation
                {
                    Id = id,
                    WhatsappNumberId = AnaNumberId,
                    SellerId = AnaId,
                    ContactId = contactId,
                    StartedByContact = true,
                    StartedAt = start,
                    LastMessageAt = start.AddMinutes(30),
                });
                db.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = id,
                    WhatsappNumberId = AnaNumberId,
                    SellerId = AnaId,
                    WaMessageId = $"f-{index}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "quanto custa?",
                    Timestamp = start,
                });

                index++;
            }

            // A etiqueta diz venda na primeira; a IA vai discordar dela.
            db.Add(new ConversationOutcome
            {
                Id = Guid.NewGuid(),
                ConversationId = SoldConversation,
                OutcomeTypeCode = OutcomeTypeCodes.Sale,
                MarkedAt = PeriodStart.AddDays(1),
            });

            return Task.CompletedTask;
        });
    }

    // A IA lê a primeira como perdida (a etiqueta diz venda → divergente) e a
    // segunda como perdida sem etiqueta nenhuma (→ convergente não é: sem
    // etiqueta e com status, também diverge). Por isso a segunda vai como venda.
    private async Task AnalyzeAsync()
    {
        // Uma rodada por conversa: dentro da mesma rodada as leituras saem em
        // paralelo e não dá para saber qual resposta cai em qual conversa.
        await AnalyzeOneAsync(SoldConversation, Answer("lost", "preco", recontact: true));
        await AnalyzeOneAsync(LostConversation, Answer(OutcomeTypeCodes.Sale, null, recontact: false));
    }

    private async Task AnalyzeOneAsync(Guid conversationId, string answer)
    {
        FakeAi.Enqueue(answer);

        var created = await Client.PostAsJsonAsync("/api/v1/ai/analyses/run",
            new { from = PeriodStart, to = PeriodEnd, conversationIds = new[] { conversationId } });
        created.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();
    }

    private async Task<AiAnalysisPageDto> ListAsync(string extra = "") =>
        (await Client.GetFromJsonAsync<AiAnalysisPageDto>($"/api/v1/ai/analyses?{Query}{extra}"))!;

    // Filtro por vendedor: a tela do gestor abre um vendedor por vez.
    [Fact]
    public async Task List_FiltersBySeller()
    {
        await SeedAsync();
        await AnalyzeAsync();

        Assert.Equal(2, (await ListAsync($"&sellerId={AnaId}")).Total);
        Assert.Equal(0, (await ListAsync($"&sellerId={BrunoId}")).Total);
    }

    // Filtro por motivo da perda: é o que agrupa "todo mundo que saiu por preço".
    [Fact]
    public async Task List_FiltersByLossReason()
    {
        await SeedAsync();
        await AnalyzeAsync();

        var byPrice = await ListAsync("&lossReason=preco");
        var row = Assert.Single(byPrice.Items);
        Assert.Equal(SoldConversation, row.ConversationId);
        Assert.Equal(0, (await ListAsync("&lossReason=prazo")).Total);
    }

    // Filtro de recontato: a lista de quem vale a pena chamar de novo.
    [Fact]
    public async Task List_FiltersByRecontact()
    {
        await SeedAsync();
        await AnalyzeAsync();

        Assert.Equal(SoldConversation, Assert.Single((await ListAsync("&recontact=true")).Items).ConversationId);
        Assert.Equal(LostConversation, Assert.Single((await ListAsync("&recontact=false")).Items).ConversationId);
    }

    // Divergência: IA ≠ etiqueta é etiquetagem esquecida, e é o filtro que dá
    // valor à tela. Conversa etiquetada como venda e lida como perdida diverge;
    // conversa sem etiqueta lida como venda também.
    [Fact]
    public async Task List_FiltersByDivergence()
    {
        await SeedAsync();
        await AnalyzeAsync();

        var divergent = await ListAsync("&divergent=true");
        Assert.Equal(2, divergent.Total);
        Assert.All(divergent.Items, r => Assert.True(r.Divergent));

        Assert.Equal(0, (await ListAsync("&divergent=false")).Total);
    }

    // Filtro por status usa o código do catálogo, não o rótulo da tela.
    [Fact]
    public async Task List_FiltersByStatusCode()
    {
        await SeedAsync();
        await AnalyzeAsync();

        Assert.Equal(SoldConversation, Assert.Single((await ListAsync("&status=lost")).Items).ConversationId);
    }

    // As sínteses também filtram por vendedor: o gestor abre uma de cada vez.
    [Fact]
    public async Task Syntheses_FilterBySeller()
    {
        await SeedAsync();
        await AnalyzeAsync();

        FakeAi.Always(JsonSerializer.Serialize(new
        {
            overview = "boa condução",
            strengths = new[] { "responde rápido" },
            improvements = new[] { "pedir a venda" },
            dominantLossPattern = "preço",
            trainingSuggestion = "treinar objeção de preço",
        }));

        var created = await Client.PostAsJsonAsync("/api/v1/ai/syntheses/run", new { from = PeriodStart, to = PeriodEnd });
        created.EnsureSuccessStatusCode();
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();

        var mine = await Client.GetFromJsonAsync<List<AiSynthesisDto>>($"/api/v1/ai/syntheses?{Query}&sellerId={AnaId}");
        Assert.Single(mine!);

        var others = await Client.GetFromJsonAsync<List<AiSynthesisDto>>($"/api/v1/ai/syntheses?{Query}&sellerId={BrunoId}");
        Assert.Empty(others!);
    }

    // Os motivos de perda do filtro vêm do mesmo enum fechado do schema, com o
    // rótulo já traduzido: a tela não pode oferecer um motivo que a IA nunca
    // poderia responder.
    [Fact]
    public async Task LossReasons_ComeFromTheClosedSchema()
    {
        var reasons = await Client.GetFromJsonAsync<List<LossReasonDto>>("/api/v1/ai/loss-reasons");

        Assert.Equal(AiAnalysisSchema.LossReasons.Length, reasons!.Count);
        var price = Assert.Single(reasons, r => r.Code == "preco");
        Assert.False(string.IsNullOrWhiteSpace(price.Label));
    }

    private sealed record LossReasonDto(string Code, string Label);
}
