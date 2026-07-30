using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.ReportExport;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.ReportExport;

public class ReportExportTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);

    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-000000000001");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-000000000001");

    private static readonly string AiAnswer = JsonSerializer.Serialize(new
    {
        status = "lost",
        confidence = 0.9,
        evidence = "achei caro",
        lossReason = "preco",
        askedForSale = false,
        ignoredBuyingSignal = true,
        objections = new[] { "preço alto" },
        shouldRecontact = true,
        recontactReason = "reclamou do preço",
        suggestedMessage = "consigo melhorar a condição",
        interest = "kit",
        summary = "cliente achou caro e sumiu",
        conductAlert = (string?)null,
    });

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
                InstanceName = "mv-5511900001111",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            for (var i = 0; i < conversations; i++)
            {
                var contactId = Guid.NewGuid();
                var conversationId = Guid.NewGuid();
                var start = PeriodStart.AddDays(1).AddHours(i);

                db.Add(new Contact
                {
                    Id = contactId,
                    RemoteJid = $"55119777760{i:D2}@s.whatsapp.net",
                    PushName = $"Cliente {i}",
                    CreatedAt = start,
                });
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
                    WaMessageId = $"in-{i}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "quanto custa o kit?",
                    Timestamp = start,
                });
                db.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    WhatsappNumberId = NumberId,
                    WaMessageId = $"out-{i}",
                    Direction = MessageDirection.Outbound,
                    Type = "conversation",
                    Text = "custa R$ 200",
                    Timestamp = start.AddMinutes(30),
                });
            }

            return Task.CompletedTask;
        });
    }

    private object Body(bool includeAi = false, string[]? charts = null, Guid[]? sellerIds = null) => new
    {
        from = PeriodStart,
        to = PeriodEnd,
        charts = charts ?? [],
        sellerIds = sellerIds ?? [],
        includeAi,
    };

    private static readonly Guid OtherSellerId = Guid.Parse("5e11e000-0000-0000-0000-000000000002");

    private async Task SeedOtherSellerAsync()
    {
        await SeedAsync(db =>
        {
            var numberId = Guid.NewGuid();
            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var start = PeriodStart.AddDays(2);

            db.Add(new Seller { Id = OtherSellerId, Name = "Bruno", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = numberId,
                SellerId = OtherSellerId,
                Phone = "5511900002222",
                InstanceName = "mv-5511900002222",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });
            db.Add(new Contact { Id = contactId, RemoteJid = "5511966665555@s.whatsapp.net", PushName = "Outro", CreatedAt = start });
            db.Add(new Conversation
            {
                Id = conversationId,
                WhatsappNumberId = numberId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = start,
                LastMessageAt = start.AddMinutes(10),
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = numberId,
                WaMessageId = "outro-1",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "oi",
                Timestamp = start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task<int> RunAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IReportExportRunner>().ProcessPendingAsync();
    }

    private static async Task<XLWorkbook> WorkbookOf(HttpResponseMessage response) =>
        new(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));

    // O pedido responde na hora com 202 e o arquivo fica pronto na passada do
    // serviço em background.
    [Fact]
    public async Task Export_IsAcceptedAndProducesTheFile()
    {
        await SeedAsync();

        var response = await Client.PostAsJsonAsync("/api/v1/reports/export", Body(charts: ["sales"]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ReportExportDto>();
        Assert.Equal("Pending", created!.Status);

        Assert.Equal(1, await RunAsync());

        var status = await Client.GetFromJsonAsync<ReportExportDto>($"/api/v1/reports/export/{created.Id}");
        Assert.Equal("Completed", status!.Status);
        Assert.True(status.FileAvailable);
        Assert.Equal("relatorio-2026-07-23-a-2026-07-30.xlsx", status.FileName);

        var file = await Client.GetAsync($"/api/v1/reports/export/{created.Id}/file");
        file.EnsureSuccessStatusCode();
        using var workbook = await WorkbookOf(file);
        Assert.True(workbook.Worksheets.Contains("Ranking"));
        Assert.Equal("Ana", workbook.Worksheet("Ranking").Cell(2, 1).GetString());
    }

    // Regressão: filtrar por vendedor derrubava a exportação com 500. O Contains
    // era aplicado depois da projeção e o EF não traduzia a expressão (reportado
    // ao testar a tela em 30/07/2026).
    [Fact]
    public async Task Export_FilteredBySeller_KeepsOnlyThatSeller()
    {
        await SeedAsync();
        await SeedOtherSellerAsync();

        var estimate = await Client.PostAsJsonAsync("/api/v1/reports/export/estimate", Body(includeAi: true, sellerIds: [SellerId]));
        estimate.EnsureSuccessStatusCode();
        var cost = await estimate.Content.ReadFromJsonAsync<ReportExportEstimate>();
        Assert.Equal(2, cost!.Conversations);

        var created = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body(sellerIds: [SellerId])))
            .Content.ReadFromJsonAsync<ReportExportDto>();
        await RunAsync();

        var status = await Client.GetFromJsonAsync<ReportExportDto>($"/api/v1/reports/export/{created!.Id}");
        Assert.Equal("Completed", status!.Status);

        using var workbook = await WorkbookOf(await Client.GetAsync($"/api/v1/reports/export/{created.Id}/file"));
        var ranking = workbook.Worksheet("Ranking");
        Assert.Equal("Ana", ranking.Cell(2, 1).GetString());
        Assert.True(ranking.Cell(3, 1).IsEmpty());
    }

    // Baixar antes de a planilha existir devolve 409 com o motivo, não um arquivo
    // vazio que o usuário abriria sem entender.
    [Fact]
    public async Task File_BeforeItIsReady_Returns409()
    {
        await SeedAsync();
        var created = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body()))
            .Content.ReadFromJsonAsync<ReportExportDto>();

        var response = await Client.GetAsync($"/api/v1/reports/export/{created!.Id}/file");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Período invertido é recusado antes de virar job.
    [Fact]
    public async Task Export_WithInvertedRange_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/reports/export", new { from = PeriodEnd, to = PeriodStart });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Com IA ligada, cada conversa vira uma linha com o status lido pelo modelo e a
    // divergência em relação à etiqueta — aqui não há etiqueta e a IA diz perdida.
    [Fact]
    public async Task Export_WithAi_FillsTheAnalysisSheet()
    {
        await SeedAsync();
        FakeAi.Always(AiAnswer);

        var created = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportDto>();
        await RunAsync();

        var file = await Client.GetAsync($"/api/v1/reports/export/{created!.Id}/file");
        using var workbook = await WorkbookOf(file);
        var sheet = workbook.Worksheet("IA — Conversas");

        Assert.Equal("Clientes perdidos", sheet.Cell(2, 8).GetString());
        Assert.Equal("—", sheet.Cell(2, 7).GetString());
        Assert.Equal("Sim", sheet.Cell(2, 10).GetString());
        Assert.Equal("Preço", sheet.Cell(2, 12).GetString());
        Assert.Equal("Não", sheet.Cell(2, 13).GetString());

        var status = await Client.GetFromJsonAsync<ReportExportDto>($"/api/v1/reports/export/{created.Id}");
        Assert.Equal(2, status!.AnalyzedConversations);
        Assert.True(status.CostBrl > 0);
    }

    // Saldo estourado no meio não invalida a planilha: ela sai com o que deu, e as
    // conversas restantes aparecem com o motivo em vez de sumirem.
    [Fact]
    public async Task Export_WhenBudgetRunsOut_StillDeliversTheSpreadsheet()
    {
        await SeedAsync();
        FakeAi.Always(AiAnswer);
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiBudget>().TryReserveAsync("teste", "fake-model", 1m);

        var created = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportDto>();
        await RunAsync();

        var file = await Client.GetAsync($"/api/v1/reports/export/{created!.Id}/file");
        file.EnsureSuccessStatusCode();
        using var workbook = await WorkbookOf(file);
        var sheet = workbook.Worksheet("IA — Conversas");

        Assert.Equal("—", sheet.Cell(2, 8).GetString());
        Assert.Equal("Saldo de IA insuficiente.", sheet.Cell(2, 22).GetString());
        Assert.Equal(0, FakeAi.CallCount);
    }

    // Regressão: com a cota do provedor estourada a fase de IA ficava minutos em
    // 429 e a planilha nunca chegava — o usuário desistia achando que travou.
    // Agora a fase tem prazo: estourado, o arquivo sai com o que deu e o que
    // faltou vai marcado (reportado ao testar a tela em 30/07/2026).
    [Fact]
    public async Task Export_WhenAiTakesTooLong_DeliversTheSpreadsheetAnyway()
    {
        await SeedAsync();
        // Toda chamada devolve 429 pedindo espera: sem prazo, o job ficaria preso.
        for (var i = 0; i < 20; i++)
            FakeAi.EnqueueStatus(HttpStatusCode.TooManyRequests, """
                {"error":{"code":429,"message":"quota","details":[{"retryDelay":"1s"}]}}
                """);

        using var host = Factory.WithWebHostBuilder(b => b.UseSetting("ReportExport:AiDeadlineSeconds", "5"));
        var client = host.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportDto>();

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IReportExportRunner>().ProcessPendingAsync();

        var status = await client.GetFromJsonAsync<ReportExportDto>($"/api/v1/reports/export/{created!.Id}");
        Assert.Equal("Completed", status!.Status);
        Assert.True(status.FileAvailable);
        Assert.Null(status.Phase);

        using var workbook = await WorkbookOf(await client.GetAsync($"/api/v1/reports/export/{created.Id}/file"));
        var sheet = workbook.Worksheet("IA — Conversas");
        // A conversa fica na planilha, com o motivo — nunca some em silêncio.
        Assert.Equal("—", sheet.Cell(2, 8).GetString());
        Assert.False(string.IsNullOrWhiteSpace(sheet.Cell(2, 22).GetString()));
    }

    // Regressão: a fila era serial e uma exportação com IA presa em limite de cota
    // segurava as outras. Medido em 30/07/2026: a planilha sem IA levava 0,2s para
    // ser gerada, mas ficou 64s parada atrás de um job de 202s.
    [Fact]
    public async Task Export_SlowJob_DoesNotBlockTheOthers()
    {
        await SeedAsync();
        // Toda chamada de IA espera e falha: o job com IA fica lento de propósito.
        for (var i = 0; i < 30; i++)
            FakeAi.EnqueueStatus(HttpStatusCode.TooManyRequests, """
                {"error":{"code":429,"message":"quota","details":[{"retryDelay":"2s"}]}}
                """);

        var lento = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportDto>();
        var rapido = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body()))
            .Content.ReadFromJsonAsync<ReportExportDto>();

        await RunAsync();

        // Os dois terminam na mesma passada; o rápido não espera o lento acabar.
        foreach (var id in new[] { lento!.Id, rapido!.Id })
        {
            var status = await Client.GetFromJsonAsync<ReportExportDto>($"/api/v1/reports/export/{id}");
            Assert.Equal("Completed", status!.Status);
            Assert.True(status.FileAvailable);
        }
    }

    // A prévia diz quanto vai custar e quantas conversas já estão analisadas — sem
    // isso o usuário confirma no escuro.
    [Fact]
    public async Task Estimate_ReportsCostAndCacheReuse()
    {
        await SeedAsync();
        FakeAi.Always(AiAnswer);

        var before = await (await Client.PostAsJsonAsync("/api/v1/reports/export/estimate", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportEstimate>();

        Assert.Equal(2, before!.Conversations);
        Assert.Equal(0, before.Cached);
        Assert.True(before.EstimatedBrl > 0);
        Assert.True(before.Affordable);

        var created = await (await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportDto>();
        await RunAsync();

        var after = await (await Client.PostAsJsonAsync("/api/v1/reports/export/estimate", Body(includeAi: true)))
            .Content.ReadFromJsonAsync<ReportExportEstimate>();

        Assert.Equal(2, after!.Cached);
        Assert.Equal(0, after.ToAnalyze);
        Assert.NotNull(created);
    }

    // Reexportar o mesmo período não paga a IA de novo: o cache cobre tudo e o
    // gasto da janela fica igual.
    [Fact]
    public async Task Export_TwiceInARow_DoesNotPayTwice()
    {
        await SeedAsync();
        FakeAi.Always(AiAnswer);

        await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true));
        await RunAsync();
        var callsAfterFirst = FakeAi.CallCount;

        await Client.PostAsJsonAsync("/api/v1/reports/export", Body(includeAi: true));
        await RunAsync();

        // A segunda passada só gasta com a síntese por vendedor, nunca com as conversas.
        Assert.Equal(callsAfterFirst + 1, FakeAi.CallCount);
        Assert.Equal(2, await InDbAsync(db => db.Set<Api.Features.Ai.Analysis.ConversationAiAnalysis>().CountAsync()));
    }
}
