using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Reconciliation;

// Número fantasma: o painel dizia "conectado" e a instância nem existia na
// Evolution. O 404 caía no mesmo catch de "Evolution fora do ar" e nada mudava —
// o fantasma vivia para sempre, e reiniciar/reconectar davam erro de comunicação.
public class GhostNumberTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("9b05ec00-0000-0000-0000-0000000000aa");
    private static readonly Guid NumberId = Guid.Parse("9b05ec00-0000-0000-0000-0000000000bb");
    private const string Instance = "mv-fantasma";

    private const string MissingBody =
        """{"status":404,"error":"Not Found","response":{"message":["The instance does not exist"]}}""";

    private async Task SeedActiveNumberAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900007777",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = Start,
            });
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = NumberId,
                State = "open",
                ResultingStatus = NumberStatus.Active,
                OccurredAt = Start,
            });
            return Task.CompletedTask;
        });
    }

    private async Task<int> RunReconciliationAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var inserted = await scope.ServiceProvider.GetRequiredService<IReconciliationService>().RunOnceAsync();
        await scope.ServiceProvider.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();
        return inserted;
    }

    // Regressão do bug: instância que não existe mais (404) derruba o número para
    // Disconnected via close sintético, em vez de deixá-lo "conectado" para sempre.
    [Fact]
    public async Task MissingInstance_MarksTheNumberDisconnected()
    {
        await SeedActiveNumberAsync();
        FakeEvolution.When(HttpMethod.Get, $"/instance/connectionState/{Instance}", MissingBody, HttpStatusCode.NotFound);

        var inserted = await RunReconciliationAsync();

        Assert.Equal(1, inserted);
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));
        Assert.Equal(NumberStatus.Disconnected, number.Status);
        // A marca d'água anda: não há o que varrer numa instância inexistente.
        Assert.NotNull(number.LastReconciledAt);
    }

    // O fantasma já rebaixado não gera um evento novo a cada ciclo: o downtime
    // conta uma vez, não a cada 30 minutos.
    [Fact]
    public async Task MissingInstance_DoesNotRepeatTheEventEveryCycle()
    {
        await SeedActiveNumberAsync();
        FakeEvolution.When(HttpMethod.Get, $"/instance/connectionState/{Instance}", MissingBody, HttpStatusCode.NotFound);

        await RunReconciliationAsync();
        var insertedAgain = await RunReconciliationAsync();

        Assert.Equal(0, insertedAgain);
        var events = await InDbAsync(db => db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.WhatsappNumberId == NumberId && e.ResultingStatus == NumberStatus.Disconnected)
            .ToListAsync());
        Assert.Single(events);
    }

    // Evolution fora do ar continua sendo falha passageira: nada de rebaixar o
    // número nem de avançar a marca d'água — o ciclo seguinte recupera tudo.
    [Fact]
    public async Task EvolutionDown_LeavesTheNumberUntouched()
    {
        await SeedActiveNumberAsync();
        FakeEvolution.When(HttpMethod.Get, $"/instance/connectionState/{Instance}", "erro", HttpStatusCode.InternalServerError);

        await RunReconciliationAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));
        Assert.Equal(NumberStatus.Active, number.Status);
        Assert.Null(number.LastReconciledAt);
    }

    // A outra direção do fantasma: instância viva na Evolution sem dono aqui. A
    // varredura apaga só a órfã velha com o nosso prefixo — instância referenciada,
    // recém-criada ou de outro sistema fica de pé.
    [Fact]
    public async Task OrphanSweep_DeletesOnlyOldUnreferencedMvInstances()
    {
        await SeedActiveNumberAsync();
        await SeedAsync(db =>
        {
            db.Add(new PairingSession
            {
                Id = Guid.NewGuid(),
                SellerId = SellerId,
                InstanceName = "mv-pareando",
                Status = PairingStatus.AwaitingScan,
                Active = true,
                CreatedAt = DateTime.UtcNow,
                QuarantineFrom = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(30),
            });
            return Task.CompletedTask;
        });

        var young = DateTime.UtcNow.ToString("O");
        FakeEvolution.When(HttpMethod.Get, "/instance/fetchInstances", $$"""
            [
              {"name":"{{Instance}}","createdAt":"2026-08-01T00:00:00.000Z"},
              {"name":"mv-pareando","createdAt":"2026-08-01T00:00:00.000Z"},
              {"name":"mv-orfa-velha","createdAt":"2026-08-01T00:00:00.000Z"},
              {"name":"mv-orfa-nova","createdAt":"{{young}}"},
              {"name":"outro-sistema","createdAt":"2026-08-01T00:00:00.000Z"}
            ]
            """);

        await RunReconciliationAsync();

        var deletes = FakeEvolution.Requests
            .Where(r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/instance/delete/", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Path)
            .ToList();
        Assert.Equal(["/instance/delete/mv-orfa-velha"], deletes);
    }
}
