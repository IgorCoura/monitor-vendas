using System.Net;
using System.Net.Http.Json;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

public class NumbersEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private sealed record SellerDto(Guid Id);
    private sealed record QrDto(string? Code, string? Base64, string? PairingCode);
    private sealed record NumberDto(Guid Id, Guid SellerId, string Phone, string InstanceName, string Status);
    private sealed record CreateNumberResponseDto(NumberDto Number, QrDto? Qr);

    private async Task<Guid> CreateSellerAsync(string name = "Vendedor")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers", new { name });
        return (await response.Content.ReadFromJsonAsync<SellerDto>())!.Id;
    }

    private void StubEvolutionHappyPath()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QRDATA","base64":"data:image/png;base64,abc"}""");
    }

    // Cadastrar número cria a instância na Evolution, configura o webhook com o secret e devolve o QR.
    [Fact]
    public async Task Create_CreatesEvolutionInstance_SetsWebhook_AndReturnsQr()
    {
        var sellerId = await CreateSellerAsync();
        StubEvolutionHappyPath();

        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/numbers", new { phone = "5511999999999" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateNumberResponseDto>();
        Assert.Equal("5511999999999", body!.Number.Phone);
        Assert.Equal("mv-5511999999999", body.Number.InstanceName);
        Assert.Equal("Disconnected", body.Number.Status);
        Assert.Equal("QRDATA", body.Qr!.Code);

        var paths = FakeEvolution.Requests.Select(r => r.Path).ToList();
        Assert.Contains("/instance/create", paths);
        Assert.Contains("/webhook/set/mv-5511999999999", paths);
        Assert.Contains("/instance/connect/mv-5511999999999", paths);

        var webhookBody = FakeEvolution.Requests.First(r => r.Path.StartsWith("/webhook/set")).Body;
        Assert.Contains(IntegrationTestWebAppFactory.WebhookSecret, webhookBody);
        Assert.Contains("MESSAGES_UPSERT", webhookBody);
        Assert.Contains("CONNECTION_UPDATE", webhookBody);
    }

    // Cadastro em vendedor inexistente devolve 404 sem chamar a Evolution.
    [Fact]
    public async Task Create_ForUnknownSeller_ReturnsNotFound()
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{Guid.NewGuid()}/numbers", new { phone = "5511999999999" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(FakeEvolution.Requests);
    }

    // Telefone repetido (mesmo em outro vendedor) devolve 409 — número é único no monitor.
    [Fact]
    public async Task Create_DuplicatePhone_ReturnsConflict()
    {
        var sellerA = await CreateSellerAsync("A");
        var sellerB = await CreateSellerAsync("B");
        StubEvolutionHappyPath();

        var first = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerA}/numbers", new { phone = "5511988887777" });
        var second = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerB}/numbers", new { phone = "5511988887777" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // Telefone sem dígitos suficientes é rejeitado com 400.
    [Fact]
    public async Task Create_InvalidPhone_ReturnsBadRequest()
    {
        var sellerId = await CreateSellerAsync();

        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/numbers", new { phone = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Reconectar um número existente devolve um novo QR obtido da Evolution.
    [Fact]
    public async Task Connect_ReturnsFreshQr()
    {
        var sellerId = await CreateSellerAsync();
        StubEvolutionHappyPath();
        var created = await (await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/numbers", new { phone = "5511977776666" }))
            .Content.ReadFromJsonAsync<CreateNumberResponseDto>();

        var response = await Client.PostAsync($"/api/v1/numbers/{created!.Number.Id}/connect", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var qr = await response.Content.ReadFromJsonAsync<QrDto>();
        Assert.Equal("QRDATA", qr!.Code);
    }

    // Falha de comunicação com a Evolution vira 502, sem gravar o número no banco.
    [Fact]
    public async Task Create_WhenEvolutionFails_Returns502AndPersistsNothing()
    {
        var sellerId = await CreateSellerAsync();
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}", HttpStatusCode.InternalServerError);

        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/numbers", new { phone = "5511966665555" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var count = await InDbAsync(db => Task.FromResult(db.Set<MonitorVendas.Api.Features.Numbers.WhatsappNumber>().Count()));
        Assert.Equal(0, count);
    }
}
