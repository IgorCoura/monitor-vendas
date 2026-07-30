using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

public class HolidaysEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    // POST /holidays cadastra o feriado e GET lista ordenado por data.
    [Fact]
    public async Task Create_AndList()
    {
        var created = await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-09-07", name = "Independência" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/holidays");
        var items = list.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("2026-09-07", items[0].GetProperty("date").GetString());
        Assert.Equal("Independência", items[0].GetProperty("name").GetString());
    }

    // Data repetida devolve 409; nome vazio devolve 400.
    [Fact]
    public async Task Create_DuplicateDateOrEmptyName_IsRejected()
    {
        await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-12-25", name = "Natal" });

        var duplicate = await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-12-25", name = "Natal de novo" });
        var noName = await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-12-26", name = "" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }

    // DELETE remove o feriado; id desconhecido devolve 404.
    [Fact]
    public async Task Delete_RemovesHoliday()
    {
        var created = await (await Client.PostAsJsonAsync("/api/v1/holidays", new { date = "2026-11-20", name = "Consciência Negra" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var deleted = await Client.DeleteAsync($"/api/v1/holidays/{created.GetProperty("id").GetGuid()}");
        var missing = await Client.DeleteAsync($"/api/v1/holidays/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/holidays");
        Assert.Empty(list.EnumerateArray());
    }
}
