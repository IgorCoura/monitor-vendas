using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Tests.Infrastructure;
using Xunit.Abstractions;

namespace MonitorVendas.Tests.Performance;

// Benchmark manual (não roda no CI por padrão):
//   dotnet test --filter "Category=benchmark"
[Trait("Category", "benchmark")]
public class ReportBenchmarkTests(IntegrationTestWebAppFactory factory, ITestOutputHelper output)
    : BaseIntegrationTest(factory)
{
    // Mede o custo dos relatórios sobre uma base grande; imprime os tempos para
    // comparar antes/depois das otimizações.
    [Fact]
    public async Task Measure_ReportEndpoints_OnLargeDataset()
    {
        var periodEnd = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var shape = LargeDatasetSeeder.Shape.Default;

        var seedWatch = Stopwatch.StartNew();
        List<Guid> sellerIds = [];
        var messages = 0;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (sellerIds, messages) = await LargeDatasetSeeder.SeedAsync(db, periodEnd, shape);
        }
        seedWatch.Stop();

        var from = periodEnd.AddDays(-shape.Days).ToString("O");
        var to = periodEnd.ToString("O");
        var rankingUrl = $"/api/v1/reports/ranking?from={from}&to={to}";
        var sellerUrl = $"/api/v1/reports/sellers/{sellerIds[0]}?from={from}&to={to}";

        // Aquecimento (JIT, pool de conexões, plano de query).
        (await Client.GetAsync(rankingUrl)).EnsureSuccessStatusCode();

        var rankingTimes = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await Client.GetAsync(rankingUrl);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            watch.Stop();
            rankingTimes.Add(watch.ElapsedMilliseconds);
            Assert.Equal(shape.Sellers, body.EnumerateArray().Count());
        }

        var sellerWatch = Stopwatch.StartNew();
        (await Client.GetAsync(sellerUrl)).EnsureSuccessStatusCode();
        sellerWatch.Stop();

        // Fecha o agregado diário (o que o serviço em background faz) e mede de novo.
        var aggregateWatch = Stopwatch.StartNew();
        int aggregatedRows;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var builder = scope.ServiceProvider.GetRequiredService<DailyMetricsBuilder>();
            var numberIds = await db.Set<WhatsappNumber>().Select(n => n.Id).ToListAsync();
            var targets = new List<(Guid, DateOnly)>();
            for (var day = builder.LocalDayOf(periodEnd.AddDays(-shape.Days - 1)); day <= builder.LocalDayOf(periodEnd); day = day.AddDays(1))
                targets.AddRange(numberIds.Select(id => (id, day)));
            aggregatedRows = await builder.RebuildAsync(targets, default);
        }
        aggregateWatch.Stop();

        var aggClient = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Metrics:UseDailyAggregates", "true");
            b.UseSetting("Metrics:LiveCalculationMaxDays", "1");
        }).CreateClient();

        (await aggClient.GetAsync(rankingUrl)).EnsureSuccessStatusCode();
        var aggTimes = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            (await aggClient.GetAsync(rankingUrl)).EnsureSuccessStatusCode();
            watch.Stop();
            aggTimes.Add(watch.ElapsedMilliseconds);
        }

        output.WriteLine("=== BENCHMARK RELATÓRIOS ===");
        output.WriteLine($"Base: {shape.Sellers} vendedores x {shape.NumbersPerSeller} números x {shape.Days} dias = {messages} mensagens (seed em {seedWatch.ElapsedMilliseconds} ms)");
        output.WriteLine($"AO VIVO  /reports/ranking : {string.Join(" ms, ", rankingTimes)} ms (mediana {rankingTimes.Order().ElementAt(rankingTimes.Count / 2)} ms)");
        output.WriteLine($"AO VIVO  /reports/sellers : {sellerWatch.ElapsedMilliseconds} ms");
        output.WriteLine($"Agregação de {aggregatedRows} linhas dia/número: {aggregateWatch.ElapsedMilliseconds} ms (roda em background)");
        output.WriteLine($"AGREGADO /reports/ranking : {string.Join(" ms, ", aggTimes)} ms (mediana {aggTimes.Order().ElementAt(aggTimes.Count / 2)} ms)");
    }
}
