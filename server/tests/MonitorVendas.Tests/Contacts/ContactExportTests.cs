using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Contacts;

public class ContactExportTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511900004444";
    private const string Customer = "5511700009999@s.whatsapp.net";

    private static long Ts(int day, int hourUtc, int minute = 0) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hourUtc, minute, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task SeedAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var numberResponse = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511900004444" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var numberId = numberResponse.GetProperty("number").GetProperty("id").GetGuid();

        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{Customer}}", "fromMe": false, "id": "X1" },
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "pushName": "Maria Silva",
                "messageTimestamp": {{Ts(1, 13)}}
              }
            }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.edit", "instance": "{{Instance}}", "data": { "labelId": "lbl-venda", "name": "venda", "color": 3 } }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}",
              "data": { "labelId": "lbl-venda", "chatId": "{{Customer}}", "type": "add" },
              "date_time": "2026-07-01T16:00:00Z" }
            """);

        await Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();
        await Client.PostAsync($"/api/v1/numbers/{numberId}/ban-permanent", null);
    }

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    // A planilha gerada é um XLSX válido: reabre com o ClosedXML e traz cabeçalho,
    // desfecho tratado, telefone como texto e a data já no fuso do relatório
    // (13:00 UTC → 10:00 em São Paulo).
    [Fact]
    public async Task Export_ProducesReadableWorkbook()
    {
        await SeedAsync();

        var response = await Client.GetAsync(
            "/api/v1/contacts/export?from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z");
        response.EnsureSuccessStatusCode();

        Assert.Equal(ContactWorkbookWriterContentType, response.Content.Headers.ContentType?.MediaType);

        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Contatos");

        Assert.Equal("Cliente", sheet.Cell(1, 1).GetString());
        Assert.Equal("Número banido?", sheet.Cell(1, 9).GetString());

        Assert.Equal("Maria Silva", sheet.Cell(2, 1).GetString());
        Assert.Equal("5511700009999", sheet.Cell(2, 2).GetString());
        Assert.Equal(new DateTime(2026, 7, 1, 10, 0, 0), sheet.Cell(2, 3).GetDateTime());
        Assert.Equal("Vendas", sheet.Cell(2, 5).GetString());
        Assert.Equal("venda", sheet.Cell(2, 6).GetString());
        Assert.Equal("Ana", sheet.Cell(2, 7).GetString());
        Assert.Equal("Sim (permanente)", sheet.Cell(2, 9).GetString());
    }

    // O filtro aplicado na exportação é o mesmo da prévia: fora do período, sai vazia.
    [Fact]
    public async Task Export_RespectsFilters()
    {
        await SeedAsync();

        var response = await Client.GetAsync(
            "/api/v1/contacts/export?from=2026-08-01T00:00:00Z&to=2026-08-31T00:00:00Z");
        response.EnsureSuccessStatusCode();

        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Contatos");

        Assert.Equal("Cliente", sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Cell(2, 1).IsEmpty());
    }

    private const string ContactWorkbookWriterContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
