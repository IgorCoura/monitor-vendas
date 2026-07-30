using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Reconciliation;

public class ReconciliationTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511999992222";
    private Guid _numberId;

    private async Task SeedNumberAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var created = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511999992222" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        _numberId = created.GetProperty("number").GetProperty("id").GetGuid();
    }

    private async Task<int> RunReconciliationAsync() =>
        await Factory.Services.GetRequiredService<IReconciliationService>().RunOnceAsync();

    private async Task ProcessAsync() =>
        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();

    // Mensagem que existe na Evolution mas não no banco (webhook perdido) é recuperada
    // pela reconciliação e persiste uma única vez, mesmo rodando o job duas vezes.
    [Fact]
    public async Task MissedMessage_IsRecoveredExactlyOnce()
    {
        await SeedNumberAsync();
        var recentTs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        // Estado "close" casa com o status Disconnected do número recém-criado:
        // o teste mede apenas a recuperação da mensagem, sem evento de estado junto.
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": {
                "records": [
                  {
                    "key": { "remoteJid": "5511777776666@s.whatsapp.net", "fromMe": false, "id": "LOST-1" },
                    "pushName": "Cliente Perdido",
                    "message": { "conversation": "webhook caiu" },
                    "messageType": "conversation",
                    "messageTimestamp": {{recentTs}}
                  }
                ]
              }
            }
            """);

        var first = await RunReconciliationAsync();
        await ProcessAsync();
        var second = await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var messages = await InDbAsync(db => db.Set<Message>().Where(m => m.WaMessageId == "LOST-1").CountAsync());
        Assert.Equal(1, messages);
    }

    // Mensagem fora da janela de lookback (antiga) não é reimportada — sem backfill.
    [Fact]
    public async Task OldMessage_OutsideLookback_IsIgnored()
    {
        await SeedNumberAsync();
        var oldTs = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": { "records": [ { "key": { "remoteJid": "x@s.whatsapp.net", "fromMe": false, "id": "OLD-1" },
                "message": { "conversation": "antiga" }, "messageType": "conversation", "messageTimestamp": {{oldTs}} } ] }
            }
            """);

        await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync(m => m.WaMessageId == "OLD-1")));
    }

    // Estado real "open" na Evolution com número marcado Disconnected → reconciliação reativa via pipeline.
    [Fact]
    public async Task StateMismatch_IsResynchronized()
    {
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");

        await RunReconciliationAsync();
        await ProcessAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.Active, number.Status);
    }
}
