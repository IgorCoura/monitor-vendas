using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Tests.Infrastructure;
using MonitorVendas.Tests.Performance;

namespace MonitorVendas.Tests.Metrics;

public class DailyAggregateTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
    private const int Days = 20;

    private static string RankingUrl =>
        $"/api/v1/reports/ranking?from={PeriodEnd.AddDays(-Days):O}&to={PeriodEnd:O}";

    private static void AssertClose(double? expected, double? actual, double tolerance = 0.0001)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected, actual);
            return;
        }

        Assert.InRange(actual.Value, expected.Value - tolerance, expected.Value + tolerance);
    }

    private HttpClient ClientWith(params (string Key, string Value)[] settings) =>
        Factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        }).CreateClient();

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await LargeDatasetSeeder.SeedAsync(db, PeriodEnd, new LargeDatasetSeeder.Shape(
            Sellers: 2, NumbersPerSeller: 1, Days: Days, ConversationsPerNumberPerDay: 2, MessagesPerConversation: 6));
    }

    // Fecha o agregado de todos os dias do período (o que o serviço em background
    // faria a partir das marcas do pipeline).
    private async Task<int> AggregateAllDaysAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = scope.ServiceProvider.GetRequiredService<DailyMetricsBuilder>();

        var numberIds = await db.Set<WhatsappNumber>().Select(n => n.Id).ToListAsync();
        var targets = new List<(Guid, DateOnly)>();
        var firstDay = builder.LocalDayOf(PeriodEnd.AddDays(-Days - 1));
        for (var day = firstDay; day <= builder.LocalDayOf(PeriodEnd); day = day.AddDays(1))
            targets.AddRange(numberIds.Select(id => (id, day)));

        return await builder.RebuildAsync(targets, default);
    }

    // O relatório somado a partir do agregado diário tem que dar os MESMOS números
    // do cálculo ao vivo (a mediana da 1ª resposta é a única aproximada).
    [Fact]
    public async Task AggregatedRead_MatchesLiveCalculation()
    {
        await SeedAsync();

        var liveClient = ClientWith(("Metrics:UseDailyAggregates", "false"));
        var live = await liveClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);

        Assert.NotNull(live);
        Assert.True(await AggregateAllDaysAsync() > 0);

        var aggClient = ClientWith(("Metrics:UseDailyAggregates", "true"), ("Metrics:LiveCalculationMaxDays", "1"));
        var aggregated = await aggClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);

        Assert.NotNull(aggregated);
        Assert.Equal(live!.Count, aggregated!.Count);

        foreach (var liveEntry in live)
        {
            var aggEntry = aggregated.Single(a => a.SellerId == liveEntry.SellerId);
            var l = liveEntry.Metrics;
            var a = aggEntry.Metrics;

            Assert.Equal(l.ConversationsStarted, a.ConversationsStarted);
            Assert.Equal(l.ConversationsAnswered, a.ConversationsAnswered);
            Assert.Equal(l.ConversationsUnanswered, a.ConversationsUnanswered);
            Assert.Equal(l.OutboundConversationsStarted, a.OutboundConversationsStarted);
            Assert.Equal(l.OutboundConversationsEngaged, a.OutboundConversationsEngaged);
            Assert.Equal(l.MessagesSent, a.MessagesSent);
            Assert.Equal(l.MessagesReceived, a.MessagesReceived);
            Assert.Equal(l.Sales, a.Sales);
            Assert.Equal(l.BanCount, a.BanCount);
            Assert.Equal(l.ResponseSamplesCount, a.ResponseSamplesCount);
            AssertClose(l.ResponseRate, a.ResponseRate);
            AssertClose(l.AvgResponseMinutes, a.AvgResponseMinutes);
            AssertClose(l.MinResponseMinutes, a.MinResponseMinutes);
            AssertClose(l.MaxResponseMinutes, a.MaxResponseMinutes);
            AssertClose(l.ReadRate, a.ReadRate);
            AssertClose(l.FollowUpRate, a.FollowUpRate);
            AssertClose(l.ConversionRate, a.ConversionRate);
            AssertClose(l.AvgTimeToCloseBusinessHours, a.AvgTimeToCloseBusinessHours, 0.01);
            AssertClose(l.EffectiveBusinessHours, a.EffectiveBusinessHours, 0.01);
            AssertClose(l.AvgSentPerBusinessHour, a.AvgSentPerBusinessHour, 0.01);
            AssertClose(l.UptimePercent, a.UptimePercent, 0.01);
            Assert.Equal(l.LastOutboundMessageAt, a.LastOutboundMessageAt);
        }
    }

    // A mediana estimada pelo histograma fica próxima da exata (dentro da faixa).
    [Fact]
    public async Task AggregatedMedian_IsCloseToExact()
    {
        await SeedAsync();
        var liveClient = ClientWith(("Metrics:UseDailyAggregates", "false"));
        var live = await liveClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);
        Assert.NotNull(live);
        await AggregateAllDaysAsync();

        var aggClient = ClientWith(("Metrics:UseDailyAggregates", "true"), ("Metrics:LiveCalculationMaxDays", "1"));
        var aggregated = await aggClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);
        Assert.NotNull(aggregated);

        var firstSeller = live!.First();
        var exact = firstSeller.Metrics.MedianFirstResponseMinutes;
        var estimated = aggregated!.Single(a => a.SellerId == firstSeller.SellerId).Metrics.MedianFirstResponseMinutes;

        Assert.NotNull(exact);
        Assert.NotNull(estimated);
        // Faixas do histograma vão até 30 min nessa região; erro aceitável.
        Assert.InRange(estimated!.Value, exact!.Value - 10, exact.Value + 10);
    }

    // Dia fechado sem linha no agregado é calculado ao vivo (nada é subnotificado)
    // e fica marcado para o serviço de agregação preencher depois.
    [Fact]
    public async Task MissingAggregateDay_FallsBackToLiveAndMarksDirty()
    {
        await SeedAsync();
        await AggregateAllDaysAsync();

        var liveClient = ClientWith(("Metrics:UseDailyAggregates", "false"));
        var expected = await liveClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);

        // Apaga o agregado de um dia no meio do período e limpa as marcas.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM dirty_metrics_days");
            var day = DateOnly.FromDateTime(PeriodEnd.AddDays(-10));
            await db.Set<DailyNumberMetrics>().Where(d => d.Day == day).ExecuteDeleteAsync();
        }

        var aggClient = ClientWith(("Metrics:UseDailyAggregates", "true"), ("Metrics:LiveCalculationMaxDays", "1"));
        var actual = await aggClient.GetFromJsonAsync<List<RankingEntryDto>>(RankingUrl);

        var expectedEntry = expected!.First();
        var actualEntry = actual!.Single(a => a.SellerId == expectedEntry.SellerId);
        Assert.Equal(expectedEntry.Metrics.MessagesSent, actualEntry.Metrics.MessagesSent);
        Assert.Equal(expectedEntry.Metrics.Sales, actualEntry.Metrics.Sales);

        var dirty = await InDbAsync(db => db.Set<DirtyMetricsDay>().CountAsync());
        Assert.True(dirty > 0);
    }
}
