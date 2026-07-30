using System.Net;
using System.Net.Http.Json;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Sellers;

public class SellersEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private sealed record SellerDto(Guid Id, string Name, bool Active, DateTime CreatedAt);

    // POST /sellers cria o vendedor e devolve 201 com o corpo preenchido.
    [Fact]
    public async Task Create_ReturnsCreatedSeller()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Maria" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var seller = await response.Content.ReadFromJsonAsync<SellerDto>();
        Assert.NotNull(seller);
        Assert.Equal("Maria", seller!.Name);
        Assert.True(seller.Active);
        Assert.NotEqual(Guid.Empty, seller.Id);
    }

    // POST /sellers sem nome é rejeitado com 400.
    [Fact]
    public async Task Create_WithoutName_ReturnsBadRequest()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // GET /sellers lista os vendedores cadastrados.
    [Fact]
    public async Task List_ReturnsRegisteredSellers()
    {
        await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" });
        await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Bruno" });

        var sellers = await Client.GetFromJsonAsync<List<SellerDto>>("/api/v1/sellers");

        Assert.NotNull(sellers);
        Assert.Equal(2, sellers!.Count);
        Assert.Contains(sellers, s => s.Name == "Ana");
        Assert.Contains(sellers, s => s.Name == "Bruno");
    }

    // GET /sellers/{id} devolve o vendedor; id desconhecido devolve 404.
    [Fact]
    public async Task GetById_ReturnsSellerOrNotFound()
    {
        var created = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Carla" }))
            .Content.ReadFromJsonAsync<SellerDto>();

        var found = await Client.GetAsync($"/api/v1/sellers/{created!.Id}");
        var missing = await Client.GetAsync($"/api/v1/sellers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // PUT /sellers/{id} altera nome e desativa o vendedor.
    [Fact]
    public async Task Update_ChangesNameAndActive()
    {
        var created = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Davi" }))
            .Content.ReadFromJsonAsync<SellerDto>();

        var response = await Client.PutAsJsonAsync($"/api/v1/sellers/{created!.Id}",
            new { name = "Davi Silva", active = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SellerDto>();
        Assert.Equal("Davi Silva", updated!.Name);
        Assert.False(updated.Active);
    }
}
