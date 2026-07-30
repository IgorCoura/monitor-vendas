using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

public class ReportsEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511999993333";
    private const string ContactA = "5511700001111@s.whatsapp.net";
    private const string ContactB = "5511700002222@s.whatsapp.net";

    private static long Ts(int day, int hourUtc, int minute = 0) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hourUtc, minute, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private Guid _sellerId;

    private async Task SeedScenarioAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedora Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        _sellerId = seller.GetProperty("id").GetGuid();
        await Client.PostAsJsonAsync($"/api/v1/sellers/{_sellerId}/numbers", new { phone = "5511999993333" });

        // Número conecta antes do período analisado.
        await PostWebhookAsync(Connection("open", 200, "2026-06-30T12:00:00Z"));

        // Conversa A: cliente pergunta 13:00Z (10h SP), vendedora responde 13:30Z; vira venda no mesmo dia.
        await PostWebhookAsync(Upsert("A1", ContactA, fromMe: false, Ts(1, 13)));
        await PostWebhookAsync(Upsert("A2", ContactA, fromMe: true, Ts(1, 13, 30)));
        await PostWebhookAsync($$"""
            { "event": "labels.edit", "instance": "{{Instance}}", "data": { "labelId": "lbl-venda", "name": "venda", "color": 3 } }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}",
              "data": { "labelId": "lbl-venda", "chatId": "{{ContactA}}", "type": "add" },
              "date_time": "2026-07-01T16:00:00Z" }
            """);

        // Conversa B: atendida em 30min, sem venda.
        await PostWebhookAsync(Upsert("B1", ContactB, fromMe: false, Ts(2, 13)));
        await PostWebhookAsync(Upsert("B2", ContactB, fromMe: true, Ts(2, 13, 30)));

        // Ban de 1 dia no meio do período.
        await PostWebhookAsync(Connection("close", 403, "2026-07-05T12:00:00Z"));
        await PostWebhookAsync(Connection("open", 200, "2026-07-06T12:00:00Z"));

        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();
    }

    private static string Upsert(string id, string jid, bool fromMe, long ts) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{Instance}}",
          "data": {
            "key": { "remoteJid": "{{jid}}", "fromMe": {{(fromMe ? "true" : "false")}}, "id": "{{id}}" },
            "message": { "conversation": "msg" },
            "messageType": "conversation",
            "messageTimestamp": {{ts}}
          }
        }
        """;

    private static string Connection(string state, int reason, string dateTime) => $$"""
        {
          "event": "connection.update",
          "instance": "{{Instance}}",
          "data": { "state": "{{state}}", "statusReason": {{reason}} },
          "date_time": "{{dateTime}}"
        }
        """;

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    // Cenário completo: 2 conversas atendidas em 30min, 1 venda, 1 ban de 1 dia —
    // o relatório do vendedor reflete cada índice.
    [Fact]
    public async Task SellerReport_ComputesAllIndicators()
    {
        await SeedScenarioAsync();

        var report = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/reports/sellers/{_sellerId}?from=2026-07-01T00:00:00Z&to=2026-07-20T00:00:00Z");

        var totals = report.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("conversationsStarted").GetInt32());
        Assert.Equal(2, totals.GetProperty("conversationsAnswered").GetInt32());
        Assert.Equal(1.0, totals.GetProperty("responseRate").GetDouble());
        Assert.Equal(30, totals.GetProperty("medianFirstResponseMinutes").GetDouble());
        Assert.Equal(2, totals.GetProperty("messagesSent").GetInt32());
        Assert.Equal(2, totals.GetProperty("messagesReceived").GetInt32());
        Assert.Equal(1, totals.GetProperty("sales").GetInt32());
        Assert.Equal(0.5, totals.GetProperty("conversionRate").GetDouble());
        Assert.Equal(1, totals.GetProperty("banCount").GetInt32());

        var uptime = totals.GetProperty("uptimePercent").GetDouble();
        Assert.InRange(uptime, 90, 99.9);

        var numbers = report.GetProperty("numbers").EnumerateArray().ToList();
        Assert.Single(numbers);
        Assert.Equal("5511999993333", numbers[0].GetProperty("phone").GetString());
    }

    // Ranking lista o vendedor ativo com as métricas agregadas do período.
    [Fact]
    public async Task Ranking_ListsSellerWithMetrics()
    {
        await SeedScenarioAsync();

        var ranking = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/reports/ranking?from=2026-07-01T00:00:00Z&to=2026-07-20T00:00:00Z");

        var entries = ranking.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("Vendedora Ana", entries[0].GetProperty("name").GetString());
        Assert.Equal(1, entries[0].GetProperty("metrics").GetProperty("sales").GetInt32());
    }

    // Vendedor inexistente devolve 404; intervalo invertido devolve 400.
    [Fact]
    public async Task Report_UnknownSellerAndInvalidRange()
    {
        var notFound = await Client.GetAsync($"/api/v1/reports/sellers/{Guid.NewGuid()}");
        var badRange = await Client.GetAsync(
            $"/api/v1/reports/sellers/{Guid.NewGuid()}?from=2026-07-20T00:00:00Z&to=2026-07-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
    }
}
