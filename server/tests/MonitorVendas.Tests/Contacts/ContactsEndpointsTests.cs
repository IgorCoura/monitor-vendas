using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Contacts;

public class ContactsEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string AnaPhone = "5511900002222";
    private const string BrunoPhone = "5511900003333";
    private const string AnaInstance = "mv-5511900002222";
    private const string BrunoInstance = "mv-5511900003333";

    private const string Maria = "5511700001111@s.whatsapp.net";
    private const string Joao = "5511700002222@s.whatsapp.net";
    private const string Carla = "5511700003333@s.whatsapp.net";

    private Guid _anaId;
    private Guid _brunoId;

    private static long Ts(int day, int hourUtc, int minute = 0) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hourUtc, minute, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // Ana atende Maria (vira venda) e Carla; Bruno atende João e depois Carla, e o
    // número dele acaba banido — cobre contato com dois vendedores e banimento.
    private async Task SeedAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        (_anaId, var anaNumberId) = await CreateSellerAsync("Ana", AnaPhone);
        (_brunoId, var brunoNumberId) = await CreateSellerAsync("Bruno", BrunoPhone);
        _ = anaNumberId;

        await PostWebhookAsync(Upsert(AnaInstance, "M1", Maria, "Maria Silva", fromMe: false, Ts(1, 13)));
        await PostWebhookAsync(Upsert(AnaInstance, "M2", Maria, null, fromMe: true, Ts(1, 13, 30)));
        await PostWebhookAsync(LabelEdit(AnaInstance, "lbl-venda", "venda"));
        await PostWebhookAsync(LabelAssociation(AnaInstance, "lbl-venda", Maria, "2026-07-01T16:00:00Z"));

        await PostWebhookAsync(Upsert(BrunoInstance, "J1", Joao, "João Souza", fromMe: false, Ts(5, 13)));

        await PostWebhookAsync(Upsert(AnaInstance, "C1", Carla, "Carla Dias", fromMe: false, Ts(2, 13)));
        await PostWebhookAsync(Upsert(BrunoInstance, "C2", Carla, "Carla Dias", fromMe: false, Ts(20, 13)));

        await ProcessAsync();
        await Client.PostAsync($"/api/v1/numbers/{brunoNumberId}/ban-permanent", null);
    }

    private async Task<(Guid SellerId, Guid NumberId)> CreateSellerAsync(string name, string phone)
    {
        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sellerId = seller.GetProperty("id").GetGuid();

        var number = await (await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/numbers", new { phone }))
            .Content.ReadFromJsonAsync<JsonElement>();

        return (sellerId, number.GetProperty("number").GetProperty("id").GetGuid());
    }

    private static string Upsert(string instance, string id, string jid, string? pushName, bool fromMe, long ts) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instance}}",
          "data": {
            "key": { "remoteJid": "{{jid}}", "fromMe": {{(fromMe ? "true" : "false")}}, "id": "{{id}}" },
            "message": { "conversation": "msg" },
            "messageType": "conversation",
            "pushName": {{(pushName is null ? "null" : $"\"{pushName}\"")}},
            "messageTimestamp": {{ts}}
          }
        }
        """;

    private static string LabelEdit(string instance, string labelId, string name) => $$"""
        { "event": "labels.edit", "instance": "{{instance}}", "data": { "labelId": "{{labelId}}", "name": "{{name}}", "color": 3 } }
        """;

    private static string LabelAssociation(string instance, string labelId, string chatId, string dateTime) => $$"""
        { "event": "labels.association", "instance": "{{instance}}",
          "data": { "labelId": "{{labelId}}", "chatId": "{{chatId}}", "type": "add" },
          "date_time": "{{dateTime}}" }
        """;

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task ProcessAsync() =>
        await Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();

    private async Task<JsonElement> ListAsync(string query = "") =>
        await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts?from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z{query}");

    private static JsonElement Row(JsonElement page, string name) =>
        page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);

    // Cada contato vira UMA linha, com nome, telefone, datas e o vendedor da conversa
    // mais recente — Carla falou com Ana e depois com Bruno, então sai como do Bruno.
    [Fact]
    public async Task List_ReturnsOneRowPerContact()
    {
        await SeedAsync();

        var page = await ListAsync();

        Assert.Equal(3, page.GetProperty("total").GetInt32());

        var maria = Row(page, "Maria Silva");
        Assert.Equal("5511700001111", maria.GetProperty("phone").GetString());
        Assert.Equal("2026-07-01T13:00:00Z", maria.GetProperty("firstMessageAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("2026-07-01T13:30:00Z", maria.GetProperty("lastMessageAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("Vendas", maria.GetProperty("outcome").GetString());
        Assert.Equal(["venda"], maria.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("Ana", maria.GetProperty("sellerName").GetString());
        Assert.Equal(AnaPhone, maria.GetProperty("sellerNumber").GetString());
        Assert.False(maria.GetProperty("numberBanned").GetBoolean());

        var carla = Row(page, "Carla Dias");
        Assert.Equal("Bruno", carla.GetProperty("sellerName").GetString());
        Assert.True(carla.GetProperty("numberBanned").GetBoolean());
        Assert.Null(carla.GetProperty("outcome").GetString());
    }

    // O período filtra E recorta: a partir de 15/07 só sobra a conversa de Carla com
    // Bruno, e a primeira mensagem dela passa a ser a do dia 20.
    [Fact]
    public async Task List_PeriodRestrictsRowsAndDates()
    {
        await SeedAsync();

        var page = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/contacts?from=2026-07-15T00:00:00Z&to=2026-07-31T00:00:00Z");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Carla Dias", items[0].GetProperty("name").GetString());
        Assert.Equal("2026-07-20T13:00:00Z", items[0].GetProperty("firstMessageAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    // Filtro por vendedor mostra só quem falou com ele — e a linha passa a refletir
    // as conversas daquele vendedor (Carla volta a aparecer como da Ana).
    [Fact]
    public async Task List_FiltersBySeller()
    {
        await SeedAsync();

        var page = await ListAsync($"&sellerId={_anaId}");

        var names = page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("Maria Silva", names);
        Assert.Contains("Carla Dias", names);
        Assert.Equal("Ana", Row(page, "Carla Dias").GetProperty("sellerName").GetString());
    }

    // Filtro por desfecho: 'sale' traz só a venda; 'none' traz quem ficou sem desfecho.
    [Fact]
    public async Task List_FiltersByOutcome()
    {
        await SeedAsync();

        var sales = await ListAsync("&outcomeTypes=sale");
        var none = await ListAsync("&outcomeTypes=none");

        Assert.Equal(["Maria Silva"], sales.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(2, none.GetProperty("total").GetInt32());
        Assert.DoesNotContain("Maria Silva", none.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
    }

    // Filtro por banimento olha o número responsável pelo contato (o da última conversa).
    [Fact]
    public async Task List_FiltersByBanned()
    {
        await SeedAsync();

        var banned = await ListAsync("&banned=true");
        var healthy = await ListAsync("&banned=false");

        Assert.Equal(2, banned.GetProperty("total").GetInt32());
        Assert.Equal(["Maria Silva"], healthy.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
    }

    // A prévia é paginada: total continua sendo o do filtro inteiro.
    [Fact]
    public async Task List_Paginates()
    {
        await SeedAsync();

        var page = await ListAsync("&page=2&pageSize=2");

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    // Intervalo invertido devolve 400.
    [Fact]
    public async Task List_InvalidRange()
    {
        var response = await Client.GetAsync("/api/v1/contacts?from=2026-07-31T00:00:00Z&to=2026-07-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
