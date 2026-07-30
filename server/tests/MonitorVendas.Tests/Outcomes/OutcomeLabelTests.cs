using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Outcomes;

public class OutcomeLabelTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511900001111";
    private const string CustomerJid = "5511777770001@s.whatsapp.net";
    private const long BaseTs = 1785243600; // 2026-07-26 13:00 UTC

    private async Task SeedNumberAndConversationAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Client.PostAsJsonAsync($"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers",
            new { phone = "5511900001111" });

        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{CustomerJid}}", "fromMe": false, "id": "L1" },
                "message": { "conversation": "quero comprar" },
                "messageType": "conversation",
                "messageTimestamp": {{BaseTs}}
              }
            }
            """);
        await ProcessAsync();
    }

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task ProcessAsync() =>
        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();

    private async Task DefineLabelAsync(string labelId, string name) =>
        await PostWebhookAsync($$"""
            { "event": "labels.edit", "instance": "{{Instance}}", "data": { "labelId": "{{labelId}}", "name": "{{name}}", "color": 1 } }
            """);

    private async Task AssociateAsync(string labelId, string type, string dateTime) =>
        await PostWebhookAsync($$"""
            { "event": "labels.association", "instance": "{{Instance}}",
              "data": { "labelId": "{{labelId}}", "chatId": "{{CustomerJid}}", "type": "{{type}}" },
              "date_time": "{{dateTime}}" }
            """);

    // Etiqueta com emoji e caixa diferente ("Venda ✅") casa com o termo "venda".
    [Fact]
    public async Task LabelWithEmoji_MatchesRegisteredTerm()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-1", "Venda ✅");
        await AssociateAsync("lbl-1", "add", "2026-07-26T16:00:00Z");
        await ProcessAsync();

        var outcome = await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync());
        Assert.Equal(OutcomeTypeCodes.Sale, outcome.OutcomeTypeCode);
    }

    // Etiqueta não mapeada fica REGISTRADA no histórico (sem virar desfecho) — é o
    // que permite reavaliar o passado quando o termo for aceito depois.
    [Fact]
    public async Task UnmappedLabel_IsRecordedWithoutOutcome()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-2", "Fechado");
        await AssociateAsync("lbl-2", "add", "2026-07-26T16:00:00Z");
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationOutcome>().CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.Set<ConversationLabel>().CountAsync()));
    }

    // Aceitar "fechado" como venda faz a conversa já etiquetada virar venda na hora
    // (retroatividade a partir do histórico registrado).
    [Fact]
    public async Task AcceptingTerm_ConvertsAlreadyLabeledConversations()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-2", "Fechado");
        await AssociateAsync("lbl-2", "add", "2026-07-26T16:00:00Z");
        await ProcessAsync();
        Assert.Equal(0, await InDbAsync(db => db.Set<ConversationOutcome>().CountAsync()));

        var response = await Client.PostAsJsonAsync("/api/v1/outcome-types/sale/terms", new { term = "Fechado" });

        response.EnsureSuccessStatusCode();
        var outcome = await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync());
        Assert.Equal(OutcomeTypeCodes.Sale, outcome.OutcomeTypeCode);
    }

    // Duas etiquetas de tipos diferentes: vale a ÚLTIMA aplicada.
    [Fact]
    public async Task LastAppliedLabelWins()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-venda", "venda");
        await DefineLabelAsync("lbl-perdido", "perdido");

        await AssociateAsync("lbl-venda", "add", "2026-07-26T16:00:00Z");
        await ProcessAsync();
        Assert.Equal(OutcomeTypeCodes.Sale, (await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync())).OutcomeTypeCode);

        await AssociateAsync("lbl-perdido", "add", "2026-07-27T10:00:00Z");
        await ProcessAsync();

        var outcome = await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync());
        Assert.Equal(OutcomeTypeCodes.Lost, outcome.OutcomeTypeCode);
    }

    // Remover a etiqueta vencedora faz a anterior (ainda ativa) voltar a valer.
    [Fact]
    public async Task RemovingWinningLabel_FallsBackToPrevious()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-venda", "venda");
        await DefineLabelAsync("lbl-perdido", "perdido");
        await AssociateAsync("lbl-venda", "add", "2026-07-26T16:00:00Z");
        await AssociateAsync("lbl-perdido", "add", "2026-07-27T10:00:00Z");
        await ProcessAsync();

        await AssociateAsync("lbl-perdido", "remove", "2026-07-28T09:00:00Z");
        await ProcessAsync();

        var outcome = await InDbAsync(db => db.Set<ConversationOutcome>().SingleAsync());
        Assert.Equal(OutcomeTypeCodes.Sale, outcome.OutcomeTypeCode);
    }

    // O relatório traz a contagem por tipo — inclusive clientes perdidos.
    [Fact]
    public async Task Report_ExposesLostClientsCount()
    {
        await SeedNumberAndConversationAsync();
        await DefineLabelAsync("lbl-perdido", "perdido");
        await AssociateAsync("lbl-perdido", "add", "2026-07-26T16:00:00Z");
        await ProcessAsync();

        var ranking = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/reports/ranking?from=2026-07-20T00:00:00Z&to=2026-07-31T00:00:00Z");

        var outcomes = ranking.EnumerateArray().First().GetProperty("metrics").GetProperty("outcomes");
        var lost = outcomes.EnumerateArray().Single(o => o.GetProperty("typeCode").GetString() == "lost");
        var sale = outcomes.EnumerateArray().Single(o => o.GetProperty("typeCode").GetString() == "sale");
        Assert.Equal(1, lost.GetProperty("count").GetInt32());
        Assert.Equal("Clientes perdidos", lost.GetProperty("name").GetString());
        Assert.Equal(0, sale.GetProperty("count").GetInt32());
    }

    // Termo já usado em outro tipo é rejeitado (uma etiqueta pertence a um tipo só).
    [Fact]
    public async Task Term_AlreadyUsedInAnotherType_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/outcome-types/lost/terms", new { term = "venda" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Tipo novo criado pela API entra no relatório sem migração nem deploy.
    [Fact]
    public async Task NewType_AppearsInReportWithoutMigration()
    {
        await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" });
        var created = await Client.PostAsJsonAsync("/api/v1/outcome-types", new { code = "aguardando pagamento", name = "Aguardando pagamento" });
        created.EnsureSuccessStatusCode();

        var ranking = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/reports/ranking?from=2026-07-20T00:00:00Z&to=2026-07-31T00:00:00Z");

        var types = await Client.GetFromJsonAsync<JsonElement>("/api/v1/outcome-types");
        Assert.Contains(types.EnumerateArray(), t => t.GetProperty("code").GetString() == "aguardando-pagamento");
        // O tipo novo já sai no relatório (zerado), sem migração nem deploy.
        var outcomes = ranking.EnumerateArray().First().GetProperty("metrics").GetProperty("outcomes");
        Assert.Contains(outcomes.EnumerateArray(), o => o.GetProperty("typeCode").GetString() == "aguardando-pagamento");
    }
}
