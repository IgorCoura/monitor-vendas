using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Integrations.Evolution;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace MonitorVendas.Tests.Infrastructure;

public sealed class IntegrationTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    private Respawner? _respawner;

    public FakeEvolutionHandler FakeEvolution { get; } = new();

    public const string WebhookSecret = "test-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Webhook:Secret", WebhookSecret);
        builder.UseSetting("Webhook:PublicBaseUrl", "http://api.test");
        builder.UseSetting("Webhook:ProcessorEnabled", "false");
        builder.UseSetting("Reconciliation:Enabled", "false");
        // Cache desligado por default: o host é compartilhado entre testes e um
        // resultado cacheado vazaria de um cenário para outro. O teste do cache
        // liga via WithWebHostBuilder.
        builder.UseSetting("Metrics:CacheSeconds", "0");
        // Agregação dirigida pelos testes (DailyMetricsBuilder.ProcessDirtyDaysAsync),
        // não pelo BackgroundService — determinismo.
        builder.UseSetting("Metrics:AggregationEnabled", "false");
        // Envio de contatos também é dirigido pelos testes; sem intervalo entre as
        // mensagens (o delay existe para proteger o número em produção).
        builder.UseSetting("ContactShare:Enabled", "false");
        builder.UseSetting("ContactShare:DelayBetweenMessagesSeconds", "0");
        builder.UseSetting("Evolution:BaseUrl", "http://evolution.fake/");
        builder.UseSetting("Evolution:ApiKey", "test-key");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<EvolutionApiClient>((_, http) =>
            {
                http.BaseAddress = new Uri("http://evolution.fake/");
                http.DefaultRequestHeaders.Add("apikey", "test-key");
            }).ConfigurePrimaryHttpMessageHandler(() => FakeEvolution);
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();

        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = ["__EFMigrationsHistory"]
        });

        await _respawner.ResetAsync(connection);

        // O Respawn apaga também o catálogo semeado pela migração (dado de
        // referência, não de teste): restaura o padrão e invalida o cache do
        // matcher, que é singleton e não veria a mudança feita fora da API.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO conversation_outcome_types ("Code", "Name", "SortOrder", "Active") VALUES
                    ('sale', 'Vendas', 1, true),
                    ('lost', 'Clientes perdidos', 2, true)
                ON CONFLICT DO NOTHING;
                INSERT INTO outcome_label_terms ("Id", "OutcomeTypeCode", "Term", "NormalizedKey", "CreatedAt") VALUES
                    ('1a1e0001-0000-0000-0000-000000000001', 'sale', 'venda', 'venda', now()),
                    ('1a1e0001-0000-0000-0000-000000000002', 'lost', 'perdido', 'perdido', now())
                ON CONFLICT DO NOTHING;
                """;
            await command.ExecuteNonQueryAsync();
        }

        Services.GetRequiredService<OutcomeCatalogVersion>().Bump();
    }

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationTestWebAppFactory>;
