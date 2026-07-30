using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Tests.Infrastructure;
using Xunit.Abstractions;

namespace MonitorVendas.Tests.Performance;

// Benchmark manual (não roda no CI por padrão):
//   dotnet test --filter "Category=benchmark"
[Trait("Category", "benchmark")]
public class ContactBenchmarkTests(IntegrationTestWebAppFactory factory, ITestOutputHelper output)
    : BaseIntegrationTest(factory)
{
    // A listagem de contatos não passa pelo agregado diário (ela é por contato, não
    // por dia/número): mede a prévia e a geração da planilha sobre a base grande.
    [Fact]
    public async Task Measure_ContactEndpoints_OnLargeDataset()
    {
        var periodEnd = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var shape = LargeDatasetSeeder.Shape.Default;

        var seedWatch = Stopwatch.StartNew();
        int messages;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, messages) = await LargeDatasetSeeder.SeedAsync(db, periodEnd, shape);
        }
        seedWatch.Stop();

        var from = periodEnd.AddDays(-shape.Days).ToString("O");
        var to = periodEnd.ToString("O");
        var listUrl = $"/api/v1/contacts?from={from}&to={to}";
        var exportUrl = $"/api/v1/contacts/export?from={from}&to={to}";

        // Aquecimento (JIT, pool de conexões, plano de query).
        (await Client.GetAsync(listUrl)).EnsureSuccessStatusCode();

        var total = 0;
        var listTimes = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await Client.GetAsync(listUrl);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            watch.Stop();
            listTimes.Add(watch.ElapsedMilliseconds);
            total = body.GetProperty("total").GetInt32();
        }

        var exportWatch = Stopwatch.StartNew();
        var file = await Client.GetByteArrayAsync(exportUrl);
        exportWatch.Stop();

        output.WriteLine("=== BENCHMARK CONTATOS ===");
        output.WriteLine($"Base: {messages} mensagens, {total} contatos (seed em {seedWatch.ElapsedMilliseconds} ms)");
        output.WriteLine($"GET /contacts (prévia)  : {string.Join(" ms, ", listTimes)} ms (mediana {listTimes.Order().ElementAt(listTimes.Count / 2)} ms)");
        output.WriteLine($"GET /contacts/export    : {exportWatch.ElapsedMilliseconds} ms para {total} linhas ({file.Length / 1024} KB)");
    }
}
