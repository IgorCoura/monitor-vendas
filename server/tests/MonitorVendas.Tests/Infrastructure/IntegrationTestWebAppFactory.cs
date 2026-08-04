using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Integrations.Ai.Gemini;
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

    public FakeAiHandler FakeAi { get; } = new();

    public FakeProxyBrHandler FakeProxyBr { get; } = new();

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
        // mensagens (o jitter existe para proteger o número em produção) e sem o
        // gate de expediente (a hora em que a suíte roda não pode decidir teste).
        builder.UseSetting("ContactShare:Enabled", "false");
        builder.UseSetting("ContactShare:MinDelaySeconds", "0");
        builder.UseSetting("ContactShare:MaxDelaySeconds", "0");
        builder.UseSetting("ContactShare:BusinessHoursOnly", "false");
        builder.UseSetting("Evolution:BaseUrl", "http://evolution.fake/");
        builder.UseSetting("Evolution:ApiKey", "test-key");

        // Proxy: os background services ficam desligados e os testes dirigem
        // IProxySyncService.RunOnceAsync() e IProxyApplier.ProcessPendingAsync()
        // direto, como o resto do pipeline.
        builder.UseSetting("Proxy:Enabled", "false");
        builder.UseSetting("ProxyBr:Enabled", "false");
        builder.UseSetting("ProxyBr:BaseUrl", "http://proxybr.fake/");
        builder.UseSetting("ProxyBr:Token", "test-token");

        // Preço redondo de propósito: R$ 5,00/dólar e US$ 1,00 por milhão de
        // tokens fazem a conta de custo caber na cabeça de quem lê o teste.
        builder.UseSetting("Ai:BaseUrl", "http://ai.fake/");
        builder.UseSetting("Ai:ApiKey", "test-key");
        builder.UseSetting("Ai:Model", "fake-model");
        builder.UseSetting("Ai:UsdBrlRate", "5");
        builder.UseSetting("Ai:MaxAttempts", "1");
        builder.UseSetting("Ai:Pricing:fake-model:InputUsdPerMillion", "1");
        builder.UseSetting("Ai:Pricing:fake-model:OutputUsdPerMillion", "1");
        // Áudio a US$ 4,00 deixa a tarifa própria visível na conta do teste.
        builder.UseSetting("Ai:Pricing:fake-model:AudioInputUsdPerMillion", "4");
        builder.UseSetting("AiBudget:Enabled", "true");
        builder.UseSetting("AiBudget:AmountPerWindow", "1.00");
        builder.UseSetting("AiBudget:WindowHours", "24");
        builder.UseSetting("AiBudget:MarginPercent", "20");
        // Os jobs de IA são dirigidos pelos testes (IAiJobRunner.ProcessPendingAsync),
        // não pelo BackgroundService — determinismo.
        builder.UseSetting("AiJob:Enabled", "false");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<EvolutionApiClient>((_, http) =>
            {
                http.BaseAddress = new Uri("http://evolution.fake/");
                http.DefaultRequestHeaders.Add("apikey", "test-key");
            }).ConfigurePrimaryHttpMessageHandler(() => FakeEvolution);

            services.AddHttpClient<GeminiProvider>((_, http) =>
            {
                http.BaseAddress = new Uri("http://ai.fake/");
            }).ConfigurePrimaryHttpMessageHandler(() => FakeAi);

            services.AddHttpClient<MonitorVendas.Api.Integrations.ProxyBr.ProxyBrClient>((_, http) =>
            {
                http.BaseAddress = new Uri("http://proxybr.fake/");
            }).ConfigurePrimaryHttpMessageHandler(() => FakeProxyBr);
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
