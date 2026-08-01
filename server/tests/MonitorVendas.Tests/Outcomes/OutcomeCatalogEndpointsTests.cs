using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Outcomes;

// O catálogo de desfechos é editado pelo usuário em produção: tipo novo, termo
// novo, tipo desativado. Cada rota errada aqui vira desfecho contado no lugar
// errado — ou o tipo de venda apagado, que levaria o ranking junto.
public class OutcomeCatalogEndpointsTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private Task<HttpResponseMessage> CreateTypeAsync(string code, string name) =>
        Client.PostAsJsonAsync("/api/v1/outcome-types", new { code, name });

    private Task<HttpResponseMessage> AddTermAsync(string code, string term) =>
        Client.PostAsJsonAsync($"/api/v1/outcome-types/{code}/terms", new { term });

    private async Task<List<OutcomeTypeDto>> ListAsync() =>
        (await Client.GetFromJsonAsync<List<OutcomeTypeDto>>("/api/v1/outcome-types"))!;

    // A listagem traz os tipos com os termos de cada um — é o que a tela edita.
    [Fact]
    public async Task List_BringsTypesWithTheirTerms()
    {
        await AddTermAsync(OutcomeTypeCodes.Sale, "Fechado");

        var types = await ListAsync();

        var sale = Assert.Single(types, t => t.Code == OutcomeTypeCodes.Sale);
        Assert.Contains(sale.Terms, t => t.Term == "Fechado");
        Assert.Contains(types, t => t.Code == "lost");
    }

    // O código é normalizado (acento, caixa, espaço) porque é ele que aparece na
    // URL e no filtro do relatório.
    [Fact]
    public async Task Create_NormalizesTheCode()
    {
        var response = await CreateTypeAsync("Aguardando Pagamento", "Aguardando pagamento");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<OutcomeTypeDto>())!;
        Assert.Equal("aguardando-pagamento", created.Code);
        // Entra no fim da ordenação, atrás dos que já existiam.
        Assert.True(created.SortOrder > 1);
    }

    // Código que normaliza para vazio (só emoji/pontuação) ou nome em branco é
    // recusado: tipo sem identidade não tem como ser filtrado depois.
    [Fact]
    public async Task Create_WithoutCodeOrName_IsRejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateTypeAsync("✅", "Fechado")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateTypeAsync("pensando", "   ")).StatusCode);
    }

    // Código repetido é conflito: dois tipos com o mesmo código dividiriam os
    // desfechos entre eles sem ninguém perceber.
    [Fact]
    public async Task Create_WithAnExistingCode_Returns409()
    {
        await CreateTypeAsync("pensando", "Pensando");

        Assert.Equal(HttpStatusCode.Conflict, (await CreateTypeAsync("Pensando", "Outro nome")).StatusCode);
    }

    // Renomear e reordenar valem para a tela; desativar tira o tipo de circulação.
    [Fact]
    public async Task Update_RenamesAndDeactivates()
    {
        await CreateTypeAsync("pensando", "Pensando");

        var response = await Client.PutAsJsonAsync("/api/v1/outcome-types/pensando",
            new { name = "Em análise", active = false, sortOrder = 9 });

        response.EnsureSuccessStatusCode();
        var updated = Assert.Single(await ListAsync(), t => t.Code == "pensando");
        Assert.Equal("Em análise", updated.Name);
        Assert.False(updated.Active);
        Assert.Equal(9, updated.SortOrder);
    }

    // Nome em branco na edição é recusado; tipo inexistente é 404.
    [Fact]
    public async Task Update_WithoutNameOrType_Fails()
    {
        await CreateTypeAsync("pensando", "Pensando");

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync(
            "/api/v1/outcome-types/pensando", new { name = " ", active = true, sortOrder = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsJsonAsync(
            "/api/v1/outcome-types/nao-existe", new { name = "X", active = true, sortOrder = 1 })).StatusCode);
    }

    // O tipo de venda não pode ser removido: conversão, ranking e relatório saem
    // dele. Apagá-lo zeraria a métrica principal do produto.
    [Fact]
    public async Task Delete_OnTheSaleType_IsRefused()
    {
        var response = await Client.DeleteAsync($"/api/v1/outcome-types/{OutcomeTypeCodes.Sale}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(await ListAsync(), t => t.Code == OutcomeTypeCodes.Sale);
    }

    // Remover um tipo leva os termos dele junto — termo órfão apontaria para um
    // tipo que não existe mais e quebraria o matcher.
    [Fact]
    public async Task Delete_TakesTheTermsWithIt()
    {
        await CreateTypeAsync("pensando", "Pensando");
        await AddTermAsync("pensando", "Talvez");

        var response = await Client.DeleteAsync("/api/v1/outcome-types/pensando");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await ListAsync(), t => t.Code == "pensando");
        Assert.Equal(0, await InDbAsync(db => db.Set<OutcomeLabelTerm>().CountAsync(t => t.OutcomeTypeCode == "pensando")));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync("/api/v1/outcome-types/pensando")).StatusCode);
    }

    // Termo em tipo inexistente é 404 e termo que normaliza para vazio é 400:
    // etiqueta sem chave casaria com qualquer coisa.
    [Fact]
    public async Task AddTerm_WithoutTypeOrText_Fails()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await AddTermAsync("nao-existe", "venda")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddTermAsync(OutcomeTypeCodes.Sale, "🎉")).StatusCode);
    }

    // Termo repetido no MESMO tipo também é conflito, e a mensagem diz que já está
    // ali — sem isso o usuário fica procurando em qual tipo ele caiu.
    [Fact]
    public async Task AddTerm_TwiceInTheSameType_SaysSo()
    {
        await AddTermAsync(OutcomeTypeCodes.Sale, "Fechado");

        var response = await AddTermAsync(OutcomeTypeCodes.Sale, "fechado ✅");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("já está nesse tipo", await response.Content.ReadAsStringAsync());
    }

    // Remover termo tira a etiqueta de circulação; id que não é daquele tipo é 404.
    [Fact]
    public async Task DeleteTerm_RemovesItAndValidatesTheType()
    {
        var created = await (await AddTermAsync(OutcomeTypeCodes.Sale, "Fechado"))
            .Content.ReadFromJsonAsync<OutcomeTermDto>();

        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.DeleteAsync($"/api/v1/outcome-types/lost/terms/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/outcome-types/{OutcomeTypeCodes.Sale}/terms/{created.Id}")).StatusCode);

        var sale = Assert.Single(await ListAsync(), t => t.Code == OutcomeTypeCodes.Sale);
        Assert.DoesNotContain(sale.Terms, t => t.Id == created.Id);
    }

    // As sugestões vêm das etiquetas que existem de verdade nos WhatsApps, com o
    // uso somado por nome (o mesmo nome tem id diferente em cada instância) e o
    // tipo em que já está mapeada.
    [Fact]
    public async Task Suggestions_CountUsageByNameAndFlagWhatIsMapped()
    {
        await AddTermAsync(OutcomeTypeCodes.Sale, "Fechado");
        await SeedAsync(db =>
        {
            db.Add(new WhatsappLabel { Id = Guid.NewGuid(), InstanceName = "mv-a", LabelId = "1", Name = "Fechado" });
            db.Add(new WhatsappLabel { Id = Guid.NewGuid(), InstanceName = "mv-b", LabelId = "2", Name = "Fechado" });
            db.Add(new WhatsappLabel { Id = Guid.NewGuid(), InstanceName = "mv-a", LabelId = "3", Name = "Sem resposta" });
            return Task.CompletedTask;
        });

        var suggestions = (await Client.GetFromJsonAsync<List<LabelSuggestionDto>>("/api/v1/outcome-labels/suggestions"))!;

        var mapped = Assert.Single(suggestions, s => s.Name == "Fechado");
        Assert.Equal(OutcomeTypeCodes.Sale, mapped.MappedToTypeCode);
        var unmapped = Assert.Single(suggestions, s => s.Name == "Sem resposta");
        Assert.Null(unmapped.MappedToTypeCode);
    }
}
