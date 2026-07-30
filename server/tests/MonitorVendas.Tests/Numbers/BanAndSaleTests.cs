using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

public class BanAndSaleTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511999991111";
    private const string CustomerJid = "5511888886666@s.whatsapp.net";
    private const long BaseTs = 1782648000;

    private Guid _numberId;

    private async Task SeedNumberAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var created = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511999991111" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        _numberId = created.GetProperty("number").GetProperty("id").GetGuid();
    }

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task ProcessAsync() =>
        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();

    private static string ConnectionBody(string state, int? reason) => $$"""
        {
          "event": "connection.update",
          "instance": "{{Instance}}",
          "data": { "state": "{{state}}", "statusReason": {{(reason?.ToString() ?? "null")}} }
        }
        """;

    // close com statusReason 403 marca o número como banido temporário e grava o evento de status.
    [Fact]
    public async Task Close403_MarksNumberAsBannedTemporary()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(ConnectionBody("close", 403));

        await ProcessAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.BannedTemporary, number.Status);
        var evt = await InDbAsync(db => db.Set<NumberStatusEvent>().SingleAsync());
        Assert.Equal(403, evt.StatusReason);
    }

    // Reconexão (open) depois do ban devolve o número ao status Active.
    [Fact]
    public async Task OpenAfterBan_Reactivates()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(ConnectionBody("close", 403));
        await PostWebhookAsync(ConnectionBody("open", 200));

        await ProcessAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.Active, number.Status);
    }

    // close com 401 (logout) é desconexão comum, não ban.
    [Fact]
    public async Task Close401_MarksDisconnected()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(ConnectionBody("close", 401));

        await ProcessAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.Disconnected, number.Status);
    }

    // Promoção manual a ban permanente via endpoint, com evento de status registrado.
    [Fact]
    public async Task BanPermanentEndpoint_PromotesStatus()
    {
        await SeedNumberAsync();

        var response = await Client.PostAsync($"/api/v1/numbers/{_numberId}/ban-permanent", null);

        response.EnsureSuccessStatusCode();
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.BannedPermanent, number.Status);
    }

    // Etiqueta com o nome de venda (case-insensitive) aplicada ao chat fecha a última conversa como Sale.
    [Fact]
    public async Task SaleLabelAssociation_CreatesOutcome()
    {
        await SeedNumberAsync();
        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{CustomerJid}}", "fromMe": false, "id": "S1" },
                "message": { "conversation": "quero comprar" },
                "messageType": "conversation",
                "messageTimestamp": {{BaseTs}}
              }
            }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.edit", "instance": "{{Instance}}", "data": { "labelId": "lbl-1", "name": "Venda", "color": 1 } }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}", "data": { "labelId": "lbl-1", "chatId": "{{CustomerJid}}", "type": "add" } }
            """);

        await ProcessAsync();

        var outcome = await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync());
        Assert.Equal("sale", outcome.OutcomeTypeCode);

        // Remover a etiqueta desfaz o desfecho.
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}", "data": { "labelId": "lbl-1", "chatId": "{{CustomerJid}}", "type": "remove" } }
            """);
        await ProcessAsync();
        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationOutcome>().CountAsync()));
    }

    // Etiqueta que não é a de venda (ex.: "Orçamento") não gera desfecho.
    [Fact]
    public async Task NonSaleLabel_DoesNotCreateOutcome()
    {
        await SeedNumberAsync();
        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{CustomerJid}}", "fromMe": false, "id": "S2" },
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "messageTimestamp": {{BaseTs}}
              }
            }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.edit", "instance": "{{Instance}}", "data": { "labelId": "lbl-2", "name": "Orçamento", "color": 2 } }
            """);
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}", "data": { "labelId": "lbl-2", "chatId": "{{CustomerJid}}", "type": "add" } }
            """);

        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationOutcome>().CountAsync()));
    }
}
