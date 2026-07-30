using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

// O saldo do teste é R$ 1,00 por janela com 20% de margem (ver a factory).
public class AiBudgetTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private async Task<T> WithBudgetAsync<T>(Func<AiBudget, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AiBudget>());
    }

    private async Task WithBudgetAsync(Func<AiBudget, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AiBudget>());
    }

    // Reserva que cabe no saldo passa; a que estouraria o teto volta nula, antes
    // de qualquer gasto.
    [Fact]
    public async Task Reserve_BlocksWhatDoesNotFitInTheWindow()
    {
        var first = await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.90m));
        var second = await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.20m));

        Assert.NotNull(first);
        Assert.Null(second);
    }

    // O débito definitivo vem dos tokens medidos pelo provedor, com a margem por
    // cima — e não da estimativa, que era só um teto.
    [Fact]
    public async Task Settle_ChargesRealTokensPlusMargin()
    {
        var reservation = await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.50m));

        // 1.000 + 200 tokens a US$ 1,00/milhão com câmbio 5,00 = R$ 0,006; +20% = R$ 0,0072.
        var charged = await WithBudgetAsync(b => b.SettleAsync(reservation!.Id, "fake-model", 1000, 200));
        var status = await WithBudgetAsync(b => b.GetStatusAsync());

        Assert.Equal(0.0072m, charged);
        Assert.Equal(0.0072m, status.Committed);
        Assert.Equal(0.9928m, status.Available);
    }

    // Chamada que não chegou a gerar nada devolve o dinheiro para a janela.
    [Fact]
    public async Task Release_GivesTheMoneyBack()
    {
        var reservation = await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.90m));

        await WithBudgetAsync(b => b.ReleaseAsync(reservation!.Id));
        var status = await WithBudgetAsync(b => b.GetStatusAsync());

        Assert.Equal(0m, status.Committed);
        Assert.NotNull(await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.90m)));
    }

    // O saldo não acumula: gasto da janela anterior não sobra nem pesa na atual.
    [Fact]
    public async Task PreviousWindowSpend_DoesNotCarryOver()
    {
        var (start, _) = await WithBudgetAsync(b => Task.FromResult(b.CurrentWindow(DateTime.UtcNow)));


        await SeedAsync(db =>
        {
            db.Add(new AiUsage
            {
                Id = Guid.NewGuid(),
                Purpose = "janela-anterior",
                Model = "fake-model",
                Status = AiUsageStatus.Settled,
                EstimatedBrl = 5m,
                ActualBrl = 5m,
                WindowStart = start.AddDays(-1),
                CreatedAt = start.AddDays(-1),
                SettledAt = start.AddDays(-1),
            });
            return Task.CompletedTask;
        });

        var status = await WithBudgetAsync(b => b.GetStatusAsync());

        Assert.Equal(0m, status.Committed);
        Assert.Equal(1m, status.Available);
    }

    // Duas reservas simultâneas não podem ler o mesmo saldo e furar o teto juntas.
    [Fact]
    public async Task ConcurrentReservations_DoNotExceedTheLimit()
    {
        var results = await Task.WhenAll(
            WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.60m)),
            WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.60m)));

        Assert.Single(results, r => r is not null);
    }

    // A tela precisa do saldo antes de mandar exportar.
    [Fact]
    public async Task Endpoint_ReturnsTheCurrentBalance()
    {
        await WithBudgetAsync(b => b.TryReserveAsync("teste", "fake-model", 0.25m));

        var status = await Client.GetFromJsonAsync<AiBudgetStatus>("/api/v1/ai/budget");

        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.Equal(1m, status.Limit);
        Assert.Equal(0.25m, status.Committed);
        Assert.Equal(0.75m, status.Available);
        Assert.True(status.WindowEnd > status.WindowStart);
    }

    // Com o controle desligado nada é bloqueado, mas o gasto continua registrado
    // — desligar o freio não pode cegar o histórico.
    [Fact]
    public async Task WhenDisabled_DoesNotBlockButStillRecords()
    {
        using var host = Factory.WithWebHostBuilder(b => b.UseSetting("AiBudget:Enabled", "false"));
        using var scope = host.Services.CreateScope();
        var budget = scope.ServiceProvider.GetRequiredService<AiBudget>();

        var reservation = await budget.TryReserveAsync("teste", "fake-model", 999m);
        var status = await budget.GetStatusAsync();

        Assert.NotNull(reservation);
        Assert.False(status.Enabled);
        Assert.Equal(999m, status.Committed);
        Assert.Equal(1, await InDbAsync(db => Task.FromResult(db.Set<AiUsage>().Count())));
    }
}
