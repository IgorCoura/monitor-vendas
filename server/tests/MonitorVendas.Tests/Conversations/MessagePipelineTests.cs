using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Conversations;

public class MessagePipelineTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511999990000";
    private const string CustomerJid = "5511888887777@s.whatsapp.net";

    // 2026-07-01 12:00 UTC como base dos timestamps controlados dos testes.
    private const long BaseTs = 1782648000;

    private async Task SeedNumberAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Client.PostAsJsonAsync($"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers",
            new { phone = "5511999990000" });
    }

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task<int> ProcessAsync()
    {
        var processor = Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>();
        return await processor.ProcessPendingAsync();
    }

    private static string UpsertBody(string waId, bool fromMe, long timestamp, string text = "oi", string jid = CustomerJid, string instance = Instance) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instance}}",
          "data": {
            "key": { "remoteJid": "{{jid}}", "fromMe": {{(fromMe ? "true" : "false")}}, "id": "{{waId}}" },
            "pushName": "Cliente Teste",
            "message": { "conversation": "{{text}}" },
            "messageType": "conversation",
            "messageTimestamp": {{timestamp}}
          }
        }
        """;

    // Mensagem inbound cria contato (com pushName), conversa iniciada pelo cliente e a mensagem com texto.
    [Fact]
    public async Task InboundMessage_CreatesContactConversationAndMessage()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(UpsertBody("M1", fromMe: false, BaseTs));

        await ProcessAsync();

        var contact = await InDbAsync(db => db.Set<Contact>().SingleAsync());
        Assert.Equal(CustomerJid, contact.RemoteJid);
        Assert.Equal("Cliente Teste", contact.PushName);

        var conversation = await InDbAsync(db => db.Set<Conversation>().SingleAsync());
        Assert.True(conversation.StartedByContact);

        var message = await InDbAsync(db => db.Set<Message>().SingleAsync());
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("oi", message.Text);
    }

    // Resposta do vendedor (fromMe) entra como outbound na MESMA conversa.
    [Fact]
    public async Task Reply_JoinsSameConversation_AsOutbound()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(UpsertBody("M1", fromMe: false, BaseTs));
        await PostWebhookAsync(UpsertBody("M2", fromMe: true, BaseTs + 600, text: "posso ajudar"));

        await ProcessAsync();

        var conversations = await InDbAsync(db => db.Set<Conversation>().CountAsync());
        Assert.Equal(1, conversations);

        var outbound = await InDbAsync(db => db.Set<Message>().SingleAsync(m => m.WaMessageId == "M2"));
        Assert.Equal(MessageDirection.Outbound, outbound.Direction);
    }

    // Mensagem após 16 dias de silêncio abre conversa NOVA; dentro de 15 dias mantém a mesma.
    [Fact]
    public async Task SilenceWindow_SplitsConversations()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(UpsertBody("M1", fromMe: false, BaseTs));
        await PostWebhookAsync(UpsertBody("M2", fromMe: false, BaseTs + (long)TimeSpan.FromDays(10).TotalSeconds));
        await PostWebhookAsync(UpsertBody("M3", fromMe: false, BaseTs + (long)TimeSpan.FromDays(10 + 16).TotalSeconds));

        await ProcessAsync();

        var conversations = await InDbAsync(db => db.Set<Conversation>().OrderBy(c => c.StartedAt).ToListAsync());
        Assert.Equal(2, conversations.Count);

        var messagesInFirst = await InDbAsync(db => db.Set<Message>().CountAsync(m => m.ConversationId == conversations[0].Id));
        Assert.Equal(2, messagesInFirst);
    }

    // Mensagens de grupo (@g.us) são ignoradas — nenhum registro criado.
    [Fact]
    public async Task GroupMessage_IsIgnored()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(UpsertBody("G1", fromMe: false, BaseTs, jid: "120363000000000000@g.us"));

        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Set<Contact>().CountAsync()));
    }

    // Instância desconhecida não gera mensagem, mas o evento é marcado como processado.
    [Fact]
    public async Task UnknownInstance_ProcessedWithoutMessage()
    {
        await PostWebhookAsync(UpsertBody("X1", fromMe: false, BaseTs, instance: "mv-nao-cadastrada"));

        var processed = await ProcessAsync();

        Assert.Equal(1, processed);
        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync()));
    }

    // MESSAGES_UPDATE com READ preenche ReadAt (e DeliveredAt por consequência) da mensagem enviada.
    [Fact]
    public async Task ReadAck_SetsReadAndDeliveredTimestamps()
    {
        await SeedNumberAsync();
        await PostWebhookAsync(UpsertBody("OUT1", fromMe: true, BaseTs));
        await PostWebhookAsync($$"""
            {
              "event": "messages.update",
              "instance": "{{Instance}}",
              "data": { "keyId": "OUT1", "remoteJid": "{{CustomerJid}}", "fromMe": true, "status": "READ" }
            }
            """);

        await ProcessAsync();

        var message = await InDbAsync(db => db.Set<Message>().SingleAsync(m => m.WaMessageId == "OUT1"));
        Assert.NotNull(message.ReadAt);
        Assert.NotNull(message.DeliveredAt);
    }
}
