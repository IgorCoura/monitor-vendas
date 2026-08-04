using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

public class WarmupAndOptOutTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511900007777";
    private const string Destination = "5511777770000";
    private const string Period = "from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z";

    private Guid _numberId;

    private static long Ts(int day, int hour) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hour, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task SeedAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Post, "/settings/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"W-1"}}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var number = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511900007777" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        _numberId = number.GetProperty("number").GetProperty("id").GetGuid();

        await PostWebhookAsync($$"""
            { "event": "connection.update", "instance": "{{Instance}}",
              "data": { "state": "open", "statusReason": 200 }, "date_time": "2026-06-30T12:00:00Z" }
            """);

        await AddInboundAsync("M1", "5511700001111@s.whatsapp.net", "Maria", Ts(1, 13), "oi");
        await AddInboundAsync("J1", "5511700002222@s.whatsapp.net", "João", Ts(2, 13), "bom dia");
        await ProcessAsync();
    }

    private Task AddInboundAsync(string id, string jid, string name, long ts, string text) =>
        PostWebhookAsync($$"""
            {
              "event": "messages.upsert", "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{jid}}", "fromMe": false, "id": "{{id}}" },
                "message": { "conversation": "{{text}}" },
                "messageType": "conversation", "pushName": "{{name}}", "messageTimestamp": {{ts}}
              }
            }
            """);

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private Task ProcessAsync() =>
        Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();

    private Task<HttpResponseMessage> ShareAsync() =>
        Client.PostAsJsonAsync($"/api/v1/contacts/share?{Period}&confirmRisk=true",
            new { senderNumberId = _numberId, destination = Destination });

    // O número conectou: a curva de aquecimento começa a contar dali.
    [Fact]
    public async Task FirstConnection_StartsTheWarmupClock()
    {
        await SeedAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync());
        Assert.NotNull(number.WarmupStartedAt);
    }

    // Ban devolve o número ao dia 1 da curva: retomar o volume de antes é o
    // caminho mais curto para o próximo ban, e a escalada seguinte é mais longa.
    [Fact]
    public async Task Ban_RestartsTheWarmupCurve()
    {
        await SeedAsync();
        var before = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().Select(n => n.WarmupStartedAt).SingleAsync());

        await PostWebhookAsync($$"""
            { "event": "connection.update", "instance": "{{Instance}}",
              "data": { "state": "close", "statusReason": 403 }, "date_time": "2026-07-20T12:00:00Z" }
            """);
        await ProcessAsync();

        var after = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().Select(n => n.WarmupStartedAt).SingleAsync());
        Assert.NotEqual(before, after);
        Assert.Equal(new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc), after);
    }

    // Teto do dia atingido: o envio para com motivo e fica pendente para a
    // próxima janela. Metade hoje e metade amanhã é melhor que queimar o número.
    [Fact]
    public async Task Sender_StopsAtTheDailyCeiling()
    {
        await SeedAsync();
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        // Cota de zero mensagens por dia: qualquer envio já está no teto.
        using var host = Factory.WithWebHostBuilder(b => b.UseSetting("AntiBan:MaxMessagesPerDay", "0"));
        await host.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/message/sendText/"));
        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Pending", status.GetProperty("status").GetString());
    }

    // Cliente que responde "SAIR" entra em opt-out e some das listas montadas
    // depois. Além de anti-ban, é exigência da LGPD.
    [Fact]
    public async Task OptOutRequest_RemovesTheContactFromFutureLists()
    {
        await SeedAsync();

        await AddInboundAsync("M2", "5511700001111@s.whatsapp.net", "Maria", Ts(3, 13), "SAIR");
        await ProcessAsync();

        var optOut = await InDbAsync(db => db.Set<ContactOptOut>().AsNoTracking().SingleAsync());
        Assert.Equal(OptOutReason.Requested, optOut.Reason);

        var response = await ShareAsync();
        response.EnsureSuccessStatusCode();
        var share = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Sobrou só o João: a Maria pediu para sair.
        Assert.Equal(1, share.GetProperty("totalContacts").GetInt32());
    }

    // Conversa normal não vira opt-out: um falso positivo silenciaria um cliente
    // ativo, erro pior que deixar passar.
    [Fact]
    public async Task NormalMessage_DoesNotCreateOptOut()
    {
        await SeedAsync();

        await AddInboundAsync("M3", "5511700001111@s.whatsapp.net", "Maria", Ts(4, 13), "nao quero o azul");
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<ContactOptOut>().CountAsync()));
    }

    // As settings da instância são aplicadas no pareamento: readMessages ligado
    // (marcar como lido é sinal humano) e alwaysOnline desligado (presença 24/7
    // é assinatura de servidor).
    [Fact]
    public async Task Pairing_AppliesTheInstanceSettings()
    {
        await SeedAsync();

        var settings = FakeEvolution.Requests.LastOrDefault(r => r.Path.StartsWith("/settings/set/"));
        Assert.NotNull(settings);
        var body = JsonDocument.Parse(settings!.Body!).RootElement;
        Assert.True(body.GetProperty("readMessages").GetBoolean());
        Assert.False(body.GetProperty("alwaysOnline").GetBoolean());
        Assert.True(body.GetProperty("groupsIgnore").GetBoolean());
    }
}
