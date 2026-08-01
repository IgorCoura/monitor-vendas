using System.Net;
using ClosedXML.Excel;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.ReportExport;

public class ReportExportTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-7);

    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-000000000001");
    private static readonly Guid OtherSellerId = Guid.Parse("5e11e000-0000-0000-0000-000000000002");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-000000000001");

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

    private static string Url(string? charts = null, string? sellerIds = null) =>
        $"/api/v1/reports/export?from={PeriodStart:O}&to={PeriodEnd:O}" +
        (charts is null ? string.Empty : $"&charts={charts}") +
        (sellerIds is null ? string.Empty : $"&sellerIds={sellerIds}");

    private static async Task<XLWorkbook> WorkbookOf(HttpResponseMessage response) =>
        new(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));

    // A planilha sai na própria resposta: sem job, sem polling, sem id para
    // buscar depois. O nome do arquivo vem no Content-Disposition.
    [Fact]
    public async Task Export_ReturnsTheSpreadsheetInTheResponse()
    {
        await SeedAsync();

        var response = await Client.GetAsync(Url(charts: "sales"));

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "relatorio-2026-07-23-a-2026-07-30.xlsx",
            response.Content.Headers.ContentDisposition?.FileNameStar);

        using var workbook = await WorkbookOf(response);
        Assert.True(workbook.Worksheets.Contains("Ranking"));
        Assert.True(workbook.Worksheets.Contains("Gráficos"));
        Assert.Equal("Ana", workbook.Worksheet("Ranking").Cell(2, 1).GetString());
    }

    // A análise por IA saiu do relatório: a planilha do painel é só métrica
    // medida, e a leitura do modelo vive na tela de análises.
    [Fact]
    public async Task Export_HasNoAiSheets()
    {
        await SeedAsync();

        using var workbook = await WorkbookOf(await Client.GetAsync(Url()));

        Assert.DoesNotContain(workbook.Worksheets, sheet => sheet.Name.StartsWith("IA"));
        var summary = workbook.Worksheet("Resumo");
        Assert.DoesNotContain(summary.RowsUsed(), row => row.Cell(1).GetString() == "Análise por IA");
    }

    // Regressão: filtrar por vendedor derrubava a exportação com 500. O Contains
    // era aplicado depois da projeção e o EF não traduzia a expressão (reportado
    // ao testar a tela em 30/07/2026).
    [Fact]
    public async Task Export_FilteredBySeller_KeepsOnlyThatSeller()
    {
        await SeedAsync();
        await SeedOtherSellerAsync();

        var response = await Client.GetAsync(Url(sellerIds: SellerId.ToString()));

        response.EnsureSuccessStatusCode();
        using var workbook = await WorkbookOf(response);
        var ranking = workbook.Worksheet("Ranking");
        Assert.Equal("Ana", ranking.Cell(2, 1).GetString());
        Assert.True(ranking.Cell(3, 1).IsEmpty());
    }

    // Período invertido é recusado antes de montar planilha nenhuma.
    [Fact]
    public async Task Export_WithInvertedRange_Returns400()
    {
        var response = await Client.GetAsync(
            $"/api/v1/reports/export?from={PeriodEnd:O}&to={PeriodStart:O}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
