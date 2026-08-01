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
