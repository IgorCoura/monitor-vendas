using System.Net;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Contacts;

// A planilha de contatos é levada para fora do painel: o nome do arquivo diz o
// período e a situação do número diz se dá para falar com aquele cliente por ali.
public class ContactExportFiltersTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 10, 13, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000d9");

    // Um cliente por situação de número: é o que o relatório precisa distinguir.
    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });

            var index = 0;
            foreach (var status in new[] { NumberStatus.Active, NumberStatus.Disconnected, NumberStatus.BannedTemporary, NumberStatus.BannedPermanent })
            {
                var numberId = Guid.NewGuid();
                var contactId = Guid.NewGuid();
                var conversationId = Guid.NewGuid();

                db.Add(new WhatsappNumber
                {
                    Id = numberId,
                    SellerId = SellerId,
                    Phone = $"551190000{index:D4}",
                    InstanceName = $"mv-exp-{index}",
                    Status = status,
                    CreatedAt = Start,
                });
                db.Add(new Contact { Id = contactId, RemoteJid = $"55119888800{index:D2}@s.whatsapp.net", PushName = $"Cliente {index}", CreatedAt = Start });
                db.Add(new Conversation
                {
                    Id = conversationId,
                    WhatsappNumberId = numberId,
                    SellerId = SellerId,
                    ContactId = contactId,
                    StartedByContact = true,
                    StartedAt = Start,
                    LastMessageAt = Start.AddMinutes(10),
                });
                db.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    WhatsappNumberId = numberId,
                    SellerId = SellerId,
                    WaMessageId = $"e-{index}",
                    Direction = MessageDirection.Inbound,
                    Type = "conversation",
                    Text = "oi",
                    Timestamp = Start,
                });

                index++;
            }

            return Task.CompletedTask;
        });
    }

    private async Task<HttpResponseMessage> ExportAsync(string query = "")
    {
        var response = await Client.GetAsync($"/api/v1/contacts/export{query}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    // O nome do arquivo carrega o período exportado — quem abre a pasta meses
    // depois precisa saber a que recorte aquela lista se refere.
    [Fact]
    public async Task FileName_DescribesThePeriod()
    {
        await SeedAsync();

        Assert.Contains("contatos-completo.xlsx",
            (await ExportAsync()).Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("contatos-desde-2026-07-01.xlsx",
            (await ExportAsync("?from=2026-07-01T00:00:00Z")).Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("contatos-ate-2026-07-31.xlsx",
            (await ExportAsync("?to=2026-07-31T00:00:00Z")).Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("contatos-2026-07-01-a-2026-07-31.xlsx",
            (await ExportAsync("?from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z")).Content.Headers.ContentDisposition!.FileNameStar);
    }

    // Período invertido é erro de quem pediu, não planilha vazia: devolver arquivo
    // em branco pareceria "não houve cliente nenhum".
    [Fact]
    public async Task InvertedPeriod_IsRejected()
    {
        await SeedAsync();

        var export = await Client.GetAsync("/api/v1/contacts/export?from=2026-07-31T00:00:00Z&to=2026-07-01T00:00:00Z");
        var preview = await Client.GetAsync("/api/v1/contacts?from=2026-07-31T00:00:00Z&to=2026-07-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, export.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
    }

    // Cada situação do número tem rótulo próprio, e "banido?" separa banido de
    // apenas desconectado — quem vai retomar o contato precisa saber a diferença.
    [Fact]
    public async Task Workbook_SpellsOutEveryNumberSituation()
    {
        await SeedAsync();

        using var workbook = new XLWorkbook(new MemoryStream(await (await ExportAsync()).Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet("Contatos");
        var situations = sheet.Column(10).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
        var banned = sheet.Column(9).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();

        Assert.Contains("Ativo", situations);
        Assert.Contains("Desconectado", situations);
        Assert.Contains("Banido temporariamente", situations);
        Assert.Contains("Banido permanentemente", situations);
        Assert.Equal(2, banned.Count(b => b == "Não"));
        Assert.Contains("Sim (temporário)", banned);
        Assert.Contains("Sim (permanente)", banned);
    }

    // Filtrar por banido devolve só quem está com o canal fora — é a lista de quem
    // precisa ser recontatado por outro número.
    [Fact]
    public async Task BannedFilter_SelectsOnlyBlockedChannels()
    {
        await SeedAsync();

        using var workbook = new XLWorkbook(new MemoryStream(
            await (await ExportAsync("?banned=true")).Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet("Contatos");

        var situations = sheet.Column(10).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
        Assert.Equal(2, situations.Count);
        Assert.All(situations, s => Assert.StartsWith("Banido", s));
    }
}
