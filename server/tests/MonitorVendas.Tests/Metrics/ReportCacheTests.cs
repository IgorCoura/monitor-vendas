using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

public class ReportCacheTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Range = "?from=2026-07-01T00:00:00Z&to=2026-07-20T00:00:00Z";

    // Cliente com o cache ligado (o factory padrão desliga para não vazar entre testes).
    private HttpClient CachedClient() =>
        Factory.WithWebHostBuilder(b => b.UseSetting("Metrics:CacheSeconds", "60")).CreateClient();

    private async Task<int> RankingCountAsync(HttpClient client, bool fresh = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/ranking{Range}");
        if (fresh)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray().Count();
    }

    // Segunda chamada no mesmo minuto vem do cache: um vendedor criado no meio não
    // aparece. Com `Cache-Control: no-cache` (o botão de atualizar) o valor é refeito.
    [Fact]
    public async Task Ranking_IsCached_AndBypassedByNoCacheHeader()
    {
        var client = CachedClient();
        Assert.Equal(0, await RankingCountAsync(client));

        await client.PostAsJsonAsync("/api/v1/sellers", new { name = "Novo Vendedor" });

        Assert.Equal(0, await RankingCountAsync(client));
        Assert.Equal(1, await RankingCountAsync(client, fresh: true));
    }

    // Cadastrar feriado muda o relógio útil, então invalida o cache na hora
    // (versão de configuração entra na chave).
    [Fact]
    public async Task Holiday_InvalidatesCache()
    {
        var client = CachedClient();
        await SeedAsync(db => Task.FromResult(db.Add(new Seller
        {
            Id = Guid.NewGuid(),
            Name = "Vendedor",
            Active = true,
            CreatedAt = DateTime.UtcNow,
        })));

        Assert.Equal(1, await RankingCountAsync(client));

        // Desativa o vendedor direto no banco: sem invalidação, o cache seguiria com 1.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seller = db.Set<Seller>().First();
            seller.Active = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await RankingCountAsync(client));

        await client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-07-09", name = "Revolução" });

        Assert.Equal(0, await RankingCountAsync(client));
    }
}
