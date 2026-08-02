using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

public class AiAnalysisExportTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000000e");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000000e");

    private string Query => $"from={PeriodStart:O}&to={PeriodEnd:O}";

    private static string Answer(string status, string? loss = null) =>
        JsonSerializer.Serialize(new
        {
            status,
            confidence = 0.9,
            evidence = "achei caro",
            lossReason = loss,
            askedForSale = false,
            ignoredBuyingSignal = true,
            objections = new[] { "preço" },
            shouldRecontact = true,
            recontactReason = "sumiu",
            suggestedMessage = "consigo melhorar",
            interest = "kit",
            summary = $"conversa classificada como {status}",
            conductAlert = (string?)null,
        });

    private async Task SeedAsync(int conversations = 2, bool labelFirstAsSale = false)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = "mv-e",
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
                    SellerId = SellerId,
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
                    SellerId = SellerId,
                    WaMessageId = $"m-{i}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "quanto custa?",
                    Timestamp = start,
                });

                if (labelFirstAsSale && i == 0)
                {
                    db.Add(new ConversationOutcome
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = conversationId,
                        OutcomeTypeCode = "sale",
                        MarkedAt = start.AddMinutes(40),
                    });
                }
            }

            return Task.CompletedTask;
        });
    }

    private async Task AnalyzeAsync(bool? force = null)
    {
        await Client.PostAsJsonAsync("/api/v1/ai/analyses/run", new { from = PeriodStart, to = PeriodEnd, force });
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();
    }

    private static async Task<XLWorkbook> WorkbookOf(HttpResponseMessage response) =>
        new(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));

    // A exportação leva para a planilha exatamente as leituras que a tela lista —
    // e não chama a IA nenhuma vez para isso.
    [Fact]
    public async Task Export_WritesTheAnalysesAlreadyDone()
    {
        await SeedAsync();
        FakeAi.Always(Answer("lost", "preco"));
        await AnalyzeAsync();
        var callsAfterAnalysis = FakeAi.CallCount;

        var response = await Client.GetAsync($"/api/v1/ai/analyses/export?{Query}");

        response.EnsureSuccessStatusCode();
        Assert.Equal(callsAfterAnalysis, FakeAi.CallCount);
        Assert.Equal(
            "analises-ia-2026-07-23-a-2026-07-30.xlsx",
            response.Content.Headers.ContentDisposition?.FileNameStar);

        using var workbook = await WorkbookOf(response);
        var sheet = workbook.Worksheet("Análises");
        Assert.Equal("Vendedor", sheet.Cell(1, 1).GetString());
        Assert.Equal("Ana", sheet.Cell(2, 1).GetString());
        Assert.Equal("Clientes perdidos", sheet.Cell(2, 8).GetString());
        Assert.Equal("Preço", sheet.Cell(2, 12).GetString());
        Assert.True(sheet.Cell(4, 1).IsEmpty());
    }

    // A divergência entre etiqueta e IA é a coluna que justifica a planilha: aqui
    // a etiqueta diz venda e a IA diz perdida.
    [Fact]
    public async Task Export_FlagsDivergenceAgainstTheLabel()
    {
        await SeedAsync(labelFirstAsSale: true);
        FakeAi.Always(Answer("lost", "preco"));
        await AnalyzeAsync();

        using var workbook = await WorkbookOf(await Client.GetAsync($"/api/v1/ai/analyses/export?{Query}"));
        var sheet = workbook.Worksheet("Análises");
        var labelled = sheet.RowsUsed().Single(r => r.Cell(7).GetString() == "Vendas");

        Assert.Equal("Divergência", sheet.Cell(1, 10).GetString());
        Assert.Equal("Sim", labelled.Cell(10).GetString());
    }

    // Os filtros da tela valem na planilha: exportar "só vendas" não leva o resto.
    [Fact]
    public async Task Export_RespectsTheScreenFilters()
    {
        await SeedAsync();
        FakeAi.Enqueue(Answer("lost", "preco"));
        FakeAi.Enqueue(Answer("sale"));
        await AnalyzeAsync();

        using var workbook = await WorkbookOf(await Client.GetAsync($"/api/v1/ai/analyses/export?{Query}&status=sale"));
        var sheet = workbook.Worksheet("Análises");

        Assert.Equal("Vendas", sheet.Cell(2, 8).GetString());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    // A síntese do vendedor sai na segunda aba, marcada como desatualizada quando
    // as leituras mudaram depois dela.
    [Fact]
    public async Task Export_WritesTheSynthesisSheetWithStaleMark()
    {
        await SeedAsync(1);
        FakeAi.Always(Answer("lost", "preco"));
        await AnalyzeAsync();

        FakeAi.Always(JsonSerializer.Serialize(new
        {
            overview = "amostra pequena",
            strengths = new[] { "responde rápido" },
            improvements = new[] { "não pede a venda" },
        }));
        await Client.PostAsJsonAsync("/api/v1/ai/syntheses/run", new { from = PeriodStart, to = PeriodEnd });
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAiJobRunner>().ProcessPendingAsync();

        using (var atual = await WorkbookOf(await Client.GetAsync($"/api/v1/ai/analyses/export?{Query}")))
        {
            var sheet = atual.Worksheet("Sínteses");
            Assert.Equal("Ana", sheet.Cell(1, 1).GetString());
            Assert.Equal("Atual", sheet.Cell(1, 2).GetString());
            Assert.Equal("amostra pequena", sheet.Cell(2, 2).GetString());
        }

        // Reanalisar muda o conjunto de leituras: a síntese passa a descrever o
        // que já não vale, e a planilha precisa dizer isso. Vai com `force` porque
        // a conversa não recebeu mensagem nova.
        FakeAi.Always(Answer("sale"));
        await AnalyzeAsync(force: true);

        using var depois = await WorkbookOf(await Client.GetAsync($"/api/v1/ai/analyses/export?{Query}"));
        Assert.Equal("Desatualizada", depois.Worksheet("Sínteses").Cell(1, 2).GetString());
    }
}
