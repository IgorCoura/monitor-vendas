using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

public class BanAndReconnectTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000ba");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000ba");
    private const string Instance = "mv-ban";

    private async Task SeedAsync(NumberStatus status = NumberStatus.Active)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = Instance,
                Status = status,
                CreatedAt = DateTime.UtcNow,
            });

            return Task.CompletedTask;
        });
    }

    // Regressão do número fantasma: reconectar um número cuja instância sumiu da
    // Evolution respondia "falha de comunicação" (404 disfarçado). Reconectar É o
    // reparo — a instância é recriada com nome novo e o cadastro repontado.
    [Fact]
    public async Task Connect_WhenTheInstanceIsMissing_RecreatesItAndReturnsTheQr()
    {
        await SeedAsync(NumberStatus.Disconnected);
        FakeEvolution.When(HttpMethod.Get, $"/instance/connect/{Instance}",
            """{"status":404,"error":"Not Found","response":{"message":["The instance does not exist"]}}""",
            HttpStatusCode.NotFound);
        FakeEvolution.When(HttpMethod.Post, "/instance/create",
            """{"instance":{"instanceName":"nova"},"qrcode":{"code":"QR-NOVO","base64":"data:image/png;base64,novo"}}""");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/connect", null);

        response.EnsureSuccessStatusCode();
        var qr = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("QR-NOVO", qr.GetProperty("code").GetString());

        // O cadastro aponta para a instância nova, com o webhook configurado nela.
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == NumberId));
        Assert.NotEqual(Instance, number.InstanceName);
        Assert.StartsWith("mv-", number.InstanceName);
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Post && r.Path.StartsWith($"/webhook/set/{number.InstanceName}", StringComparison.OrdinalIgnoreCase));
    }

    // Regressão (01/08/2026): declarar ban permanente só mudava o status no banco.
    // A instância seguia conectada na Evolution, recebendo mensagem e contando
    // uptime de um número dado como perdido.
    [Fact]
    public async Task BanPermanent_LogsTheInstanceOut()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/ban-permanent", new { });

        response.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.StartsWith($"/instance/logout/{Instance}", StringComparison.OrdinalIgnoreCase));

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == NumberId));
        Assert.Equal(NumberStatus.BannedPermanent, number.Status);
    }

    // Evolution fora do ar não pode impedir o registro do ban: a decisão é do
    // operador e vale mesmo sem confirmação do outro lado.
    [Fact]
    public async Task BanPermanent_WhenLogoutFails_StillRecordsTheBan()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}", HttpStatusCode.InternalServerError);

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/ban-permanent", new { });

        response.EnsureSuccessStatusCode();
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == NumberId));
        Assert.Equal(NumberStatus.BannedPermanent, number.Status);
    }

    // O código de pareamento da reconexão sai de uma instância RECRIADA com o
    // número. (Regressão: `connect?number=` devolvia o código de uma sessão antiga
    // em cache, que o WhatsApp recusa — o código nunca funcionava.)
    [Fact]
    public async Task PairingCode_RecreatesTheInstanceWithTheNumber()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create",
            """{"qrcode":{"code":"QR-NOVO","base64":"data:image/png;base64,BBB","pairingCode":"ZLKPFRXL"}}""");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Delete, "/instance/delete/", "{}");
        await SeedAsync(NumberStatus.Disconnected);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/pairing-code", null);
        response.EnsureSuccessStatusCode();

        var qr = await response.Content.ReadFromJsonAsync<QrCodeDto>();
        Assert.Equal("ZLKPFRXL", qr!.PairingCode);
        Assert.Equal("QR-NOVO", qr.Code);

        // A instância nova nasceu com o número e a velha foi apagada — e é para a
        // nova que o cadastro passa a apontar, senão os webhooks deste número
        // chegariam para uma instância que não existe mais.
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Path.Contains("/instance/create", StringComparison.OrdinalIgnoreCase) &&
            (r.Body ?? string.Empty).Contains("5511900001111"));
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.Contains($"/instance/delete/{Instance}", StringComparison.OrdinalIgnoreCase));

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));
        Assert.NotEqual(Instance, number.InstanceName);
        Assert.StartsWith("mv-", number.InstanceName);
    }

    // Reconexão simples não traz código nenhum: o que a Evolution devolveria ali é
    // o de uma sessão vencida, e mostrá-lo faria o usuário perder tempo tentando.
    [Fact]
    public async Task Connect_DoesNotServeAStalePairingCode()
    {
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/",
            """{"code":"QR","base64":"data:image/png;base64,AAA","pairingCode":"VELHO123"}""");
        await SeedAsync(NumberStatus.Disconnected);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/connect", null);

        var qr = await response.Content.ReadFromJsonAsync<QrCodeDto>();
        Assert.Null(qr!.PairingCode);
        Assert.Equal("QR", qr.Code);
    }

    // Número conectado não recria instância: derrubaria a sessão viva de quem está
    // trabalhando.
    [Fact]
    public async Task PairingCode_OnAConnectedNumber_IsRefused()
    {
        await SeedAsync();

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/pairing-code", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Method == HttpMethod.Delete);
    }

    // Número banido permanente também exige confirmação para gerar o código: é a
    // mesma decisão manual da reconexão.
    [Fact]
    public async Task PairingCode_OnPermanentlyBannedNumber_RequiresConfirmation()
    {
        await SeedAsync(NumberStatus.BannedPermanent);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/pairing-code", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Ban permanente é decisão manual: reconectar sem confirmar apagaria essa
    // decisão em silêncio, então o QR nem é gerado.
    [Fact]
    public async Task Connect_OnPermanentlyBannedNumber_RequiresConfirmation()
    {
        await SeedAsync(NumberStatus.BannedPermanent);

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/connect", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/connect/", StringComparison.OrdinalIgnoreCase));
    }

    // Com a confirmação explícita, a reconexão segue: o número voltou e quem
    // opera assumiu isso.
    [Fact]
    public async Task Connect_OnPermanentlyBannedNumber_WithConfirmation_GeneratesTheQr()
    {
        await SeedAsync(NumberStatus.BannedPermanent);
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/connect?confirmBanned=true", new { });

        response.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/connect/", StringComparison.OrdinalIgnoreCase));
    }

    // Número que não está banido conecta direto, sem confirmação nenhuma.
    [Fact]
    public async Task Connect_OnNormalNumber_NeedsNoConfirmation()
    {
        await SeedAsync(NumberStatus.Disconnected);
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/connect", new { });

        response.EnsureSuccessStatusCode();
    }
}
