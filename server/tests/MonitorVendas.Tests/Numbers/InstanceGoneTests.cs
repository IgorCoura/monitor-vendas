using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// Instância apagada ou deslogada do lado da Evolution não manda connection.update
// nenhum — sem assinar REMOVE_INSTANCE/LOGOUT_INSTANCE, o número ficava
// "conectado" no painel até alguém reparar no erro de comunicação.
public class InstanceGoneTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("60fe0000-0000-0000-0000-0000000000aa");
    private static readonly Guid NumberId = Guid.Parse("60fe0000-0000-0000-0000-0000000000bb");
    private const string Instance = "mv-sumida";

    private async Task SeedAsync(NumberStatus status)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = Instance,
                Status = status,
                CreatedAt = Start,
            });
            return Task.CompletedTask;
        });
    }

    private async Task PostEventAsync(string eventName)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = eventName,
            instance = Instance,
            data = new { instanceName = Instance },
        });
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();
    }

    private Task<WhatsappNumber> NumberAsync() =>
        InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));

    // Regressão do fantasma em tempo real: remove.instance derruba o número na
    // hora, sem esperar o ciclo de 30 minutos da reconciliação.
    [Fact]
    public async Task RemoveInstance_MarksTheActiveNumberDisconnected()
    {
        await SeedAsync(NumberStatus.Active);

        await PostEventAsync("remove.instance");

        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
        var events = await InDbAsync(db => db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.WhatsappNumberId == NumberId).ToListAsync());
        var evt = Assert.Single(events);
        Assert.Equal("removed", evt.State);
        Assert.Equal(NumberStatus.Disconnected, evt.ResultingStatus);
    }

    // logout.instance é o mesmo caso: aparelho desvinculado do lado de lá.
    [Fact]
    public async Task LogoutInstance_MarksTheActiveNumberDisconnected()
    {
        await SeedAsync(NumberStatus.Active);

        await PostEventAsync("logout.instance");

        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
    }

    // Ban é estado mais forte que "sem instância": o logout que acompanha o ban
    // não pode rebaixar a decisão para um simples Disconnected.
    [Fact]
    public async Task LogoutInstance_DoesNotDemoteABannedNumber()
    {
        await SeedAsync(NumberStatus.BannedPermanent);

        await PostEventAsync("logout.instance");

        Assert.Equal(NumberStatus.BannedPermanent, (await NumberAsync()).Status);
        Assert.Empty(await InDbAsync(db => db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.WhatsappNumberId == NumberId).ToListAsync()));
    }

    // Número já desconectado não ganha evento em dobro quando a instância some:
    // dois "fora do ar" seguidos descrevem o mesmo período de downtime.
    [Fact]
    public async Task RemoveInstance_OnADisconnectedNumber_AddsNothing()
    {
        await SeedAsync(NumberStatus.Disconnected);

        await PostEventAsync("remove.instance");

        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
        Assert.Empty(await InDbAsync(db => db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.WhatsappNumberId == NumberId).ToListAsync()));
    }
}
