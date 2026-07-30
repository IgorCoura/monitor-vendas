using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Webhooks;

public class WebhookEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static string WebhookUrl(string secret = IntegrationTestWebAppFactory.WebhookSecret) =>
        $"/api/v1/webhooks/evolution/{secret}";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private const string MessageUpsertBody = """
        {
          "event": "messages.upsert",
          "instance": "mv-5511999999999",
          "data": {
            "key": { "remoteJid": "5511888887777@s.whatsapp.net", "fromMe": false, "id": "MSG-001" },
            "pushName": "Cliente",
            "message": { "conversation": "oi, quero comprar" },
            "messageType": "conversation",
            "messageTimestamp": 1753790400
          },
          "date_time": "2026-07-29T10:00:00Z"
        }
        """;

    // Payload válido é persistido como WebhookEvent com o tipo normalizado (messages.upsert → MESSAGES_UPSERT).
    [Fact]
    public async Task Post_ValidPayload_PersistsNormalizedEvent()
    {
        var response = await Client.PostAsync(WebhookUrl(), Json(MessageUpsertBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await InDbAsync(db => db.Set<WebhookEvent>().ToListAsync());
        var evt = Assert.Single(events);
        Assert.Equal("MESSAGES_UPSERT", evt.EventType);
        Assert.Equal("mv-5511999999999", evt.InstanceName);
        Assert.Null(evt.ProcessedAt);
        Assert.Contains("MSG-001", evt.DedupeKey);
    }

    // Secret errado responde 404 e não persiste nada.
    [Fact]
    public async Task Post_WrongSecret_ReturnsNotFoundAndPersistsNothing()
    {
        var response = await Client.PostAsync(WebhookUrl("wrong-secret"), Json(MessageUpsertBody));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var count = await InDbAsync(db => db.Set<WebhookEvent>().CountAsync());
        Assert.Equal(0, count);
    }

    // O mesmo messages.upsert entregue duas vezes (retry da Evolution) persiste uma única vez.
    [Fact]
    public async Task Post_DuplicateMessageUpsert_PersistsOnce()
    {
        await Client.PostAsync(WebhookUrl(), Json(MessageUpsertBody));
        var second = await Client.PostAsync(WebhookUrl(), Json(MessageUpsertBody));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var count = await InDbAsync(db => db.Set<WebhookEvent>().CountAsync());
        Assert.Equal(1, count);
    }

    // JSON inválido ou sem event/instance é rejeitado com 400.
    [Theory]
    [InlineData("not-json")]
    [InlineData("""{ "data": {} }""")]
    public async Task Post_InvalidPayload_ReturnsBadRequest(string body)
    {
        var response = await Client.PostAsync(WebhookUrl(), Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Evento sem handler registrado é marcado como processado (não fica preso na fila).
    [Fact]
    public async Task Processor_EventWithoutHandler_IsMarkedProcessed()
    {
        var body = """{ "event": "chats.update", "instance": "mv-1", "data": {} }""";
        await Client.PostAsync(WebhookUrl(), Json(body));

        using var scope = Factory.Services.CreateScope();
        var processor = Factory.Services.GetRequiredService<IWebhookProcessor>();
        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var evt = await InDbAsync(db => db.Set<WebhookEvent>().SingleAsync());
        Assert.NotNull(evt.ProcessedAt);
    }
}
