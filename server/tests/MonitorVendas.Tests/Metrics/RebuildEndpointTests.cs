using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Tests.Infrastructure;
using MonitorVendas.Tests.Performance;

namespace MonitorVendas.Tests.Metrics;

public class RebuildEndpointTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await LargeDatasetSeeder.SeedAsync(db, PeriodEnd, new LargeDatasetSeeder.Shape(
            Sellers: 1, NumbersPerSeller: 1, Days: 5, ConversationsPerNumberPerDay: 1, MessagesPerConversation: 4));
    }

    // POST /reports/rebuild fecha o agregado do intervalo e não deixa marcas pendentes.
    [Fact]
    public async Task Rebuild_FillsAggregate_AndClearsDirtyMarks()
    {
        await SeedAsync();

        var response = await Client.PostAsync("/api/v1/reports/rebuild?from=2026-07-24&to=2026-07-30", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("processed").GetInt32() > 0);

        Assert.True(await InDbAsync(db => db.Set<DailyNumberMetrics>().CountAsync()) > 0);
        Assert.Equal(0, await InDbAsync(db => db.Set<DirtyMetricsDay>().CountAsync()));
    }

    // Intervalo invertido é rejeitado.
    [Fact]
    public async Task Rebuild_InvalidRange_ReturnsBadRequest()
    {
        var response = await Client.PostAsync("/api/v1/reports/rebuild?from=2026-07-30&to=2026-07-01", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Cadastrar feriado marca os dias ao redor para refazer: o relógio útil mudou
    // e os agregados antigos daquele período não valem mais.
    [Fact]
    public async Task CreatingHoliday_MarksSurroundingDaysDirty()
    {
        await SeedAsync();
        await Client.PostAsync("/api/v1/reports/rebuild?from=2026-07-24&to=2026-07-30", null);
        Assert.Equal(0, await InDbAsync(db => db.Set<DirtyMetricsDay>().CountAsync()));

        await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-07-28", name = "Feriado local" });

        var dirty = await InDbAsync(db => db.Set<DirtyMetricsDay>().Select(d => d.Day).ToListAsync());
        Assert.Contains(new DateOnly(2026, 7, 28), dirty);
        Assert.Contains(new DateOnly(2026, 7, 27), dirty);
        Assert.Contains(new DateOnly(2026, 7, 29), dirty);
    }
}
