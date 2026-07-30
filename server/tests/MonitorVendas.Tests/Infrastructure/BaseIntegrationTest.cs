using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Tests.Infrastructure;

[Collection("integration")]
public abstract class BaseIntegrationTest(IntegrationTestWebAppFactory factory) : IAsyncLifetime
{
    protected IntegrationTestWebAppFactory Factory { get; } = factory;
    protected HttpClient Client { get; private set; } = null!;
    protected FakeEvolutionHandler FakeEvolution => Factory.FakeEvolution;

    public async Task InitializeAsync()
    {
        // CreateClient garante que o host subiu (e as migrações rodaram) antes do Respawn.
        Client = Factory.CreateClient();
        await Factory.ResetDatabaseAsync();
        FakeEvolution.Reset();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await query(db);
    }

    protected async Task SeedAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }
}
