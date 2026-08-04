using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// Desconectar e reiniciar parecem a mesma coisa e não são: o primeiro desvincula
// o aparelho (só volta com QR novo), o segundo só chacoalha o socket. Trocar um
// pelo outro tira um vendedor do ar sem querer.
public class NumberControlTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000cc");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000cc");
    private const string Instance = "mv-controle";

    // `paired` distingue "nunca conectou" de "já esteve no ar": é o evento Active
    // no histórico que diz se existe sessão para reiniciar.
    private async Task SeedAsync(NumberStatus status = NumberStatus.Active, bool paired = true, DateTime? bannedUntil = null)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900002222",
                InstanceName = Instance,
                Status = status,
                CreatedAt = Start,
                BannedUntil = bannedUntil,
            });

            if (paired)
            {
                db.Add(new NumberStatusEvent
                {
                    WhatsappNumberId = NumberId,
                    State = "open",
                    ResultingStatus = NumberStatus.Active,
                    OccurredAt = Start,
                });
            }

            return Task.CompletedTask;
        });
    }

    private Task<WhatsappNumber> NumberAsync() =>
        InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == NumberId));

    private Task<List<NumberStatusEvent>> EventsAsync() =>
        InDbAsync(db => db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.WhatsappNumberId == NumberId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync());

    // Reconectar dentro do cooldown pós-ban é recusado com a data de liberação:
    // a reconexão insistente é o que promove ban temporário a permanente.
    [Fact]
    public async Task Connect_DuringBanCooldown_Returns409WithTheDate()
    {
        var bannedUntil = DateTime.UtcNow.AddHours(20);
        await SeedAsync(NumberStatus.BannedTemporary, bannedUntil: bannedUntil);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/connect", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(bannedUntil, body.GetProperty("bannedUntil").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/connect/"));
    }

    // "Reconectar mesmo assim": o operador pode furar o cooldown, mas só dizendo
    // explicitamente que sabe o risco.
    [Fact]
    public async Task Connect_DuringCooldownWithConfirmation_Proceeds()
    {
        await SeedAsync(NumberStatus.BannedTemporary, bannedUntil: DateTime.UtcNow.AddHours(20));
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR-NOVO"}""");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/connect?confirmCooldown=true", null);

        response.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r => r.Path.StartsWith($"/instance/connect/{Instance}", StringComparison.OrdinalIgnoreCase));
    }

    // Cooldown vencido não trava nada: o prazo existe para segurar a pressa, não
    // para exigir confirmação para sempre.
    [Fact]
    public async Task Connect_AfterCooldownExpired_Proceeds()
    {
        await SeedAsync(NumberStatus.BannedTemporary, bannedUntil: DateTime.UtcNow.AddHours(-1));
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR-NOVO"}""");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/connect", null);

        response.EnsureSuccessStatusCode();
    }

    // Reiniciar sobe o socket de novo: durante um ban é mais uma tentativa de
    // voltar ao ar. Sem este aviso, o cooldown do "Reconectar" seria contornável
    // pelo botão ao lado — e confirmando, o reinício acontece.
    [Fact]
    public async Task Restart_DuringBanCooldown_WarnsAndProceedsWhenConfirmed()
    {
        await SeedAsync(NumberStatus.BannedTemporary, bannedUntil: DateTime.UtcNow.AddHours(20));
        FakeEvolution.When(HttpMethod.Post, "/instance/restart/", "{}");

        var warned = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart", null);

        Assert.Equal(HttpStatusCode.Conflict, warned.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/restart/"));

        var confirmed = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart?confirmCooldown=true", null);

        confirmed.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/restart/"));
    }

    // O código de pareamento recria a instância — furar o cooldown por ele seria
    // o mesmo erro que pelo QR. O mesmo aviso vale para os dois caminhos.
    [Fact]
    public async Task PairingCode_DuringBanCooldown_Returns409()
    {
        await SeedAsync(NumberStatus.BannedTemporary, bannedUntil: DateTime.UtcNow.AddHours(20));

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/pairing-code", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/instance/create"));
    }

    // Desconectar desvincula o aparelho na Evolution e registra o estado na hora,
    // sem esperar o webhook — é o que faz o downtime começar a contar certo.
    [Fact]
    public async Task Disconnect_LogsOutAndRecordsTheState()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/disconnect", null);

        response.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.StartsWith($"/instance/logout/{Instance}", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
        Assert.Contains(await EventsAsync(), e => e.ResultingStatus == NumberStatus.Disconnected);
    }

    // Evolution fora do ar não impede o registro: a decisão foi de quem opera, e o
    // painel não pode continuar mostrando o número como ativo.
    [Fact]
    public async Task Disconnect_WhenLogoutFails_StillRecordsIt()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "erro", HttpStatusCode.InternalServerError);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/disconnect", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
    }

    // Número que já não está conectado não tem o que desconectar — e a Evolution
    // recusaria o logout de qualquer forma.
    [Fact]
    public async Task Disconnect_OnANumberThatIsNotConnected_Returns409()
    {
        await SeedAsync(NumberStatus.Disconnected);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/disconnect", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Method == HttpMethod.Delete);
    }

    // O `connection.update` que chega depois do desconectar manual não pode contar
    // downtime em dobro: os dois eventos descrevem o mesmo canal fora do ar.
    [Fact]
    public async Task Disconnect_FollowedByTheWebhook_DoesNotDoubleTheDowntime()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");
        await Client.PostAsync($"/api/v1/numbers/{NumberId}/disconnect", null);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "connection.update",
            instance = Instance,
            data = new { instance = Instance, state = "close" },
        });
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();

        // Dois eventos de "fora do ar" em sequência, um só período de downtime.
        var events = await EventsAsync();
        Assert.Equal(2, events.Count(e => e.ResultingStatus == NumberStatus.Disconnected));

        var report = await Client.GetFromJsonAsync<SellerReportDto>(
            $"/api/v1/reports/sellers/{SellerId}?from={Start:O}&to={Start.AddHours(4):O}");

        // O número caiu logo no início da janela: fica quase tudo fora do ar, mas
        // nunca mais de 100% — contar duas vezes daria uptime negativo.
        var uptime = report!.Totals.UptimePercent;
        Assert.InRange(uptime, 0, 100);
    }

    // Reiniciar chacoalha o socket sem desvincular: o status continua sendo
    // decidido pelo `connection.update`, não pelo clique.
    [Fact]
    public async Task Restart_DoesNotTouchTheStatus()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Post, "/instance/restart/", "{}");
        var before = await EventsAsync();

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart", null);

        response.EnsureSuccessStatusCode();
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Post && r.Path.StartsWith($"/instance/restart/{Instance}", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(NumberStatus.Active, (await NumberAsync()).Status);
        Assert.Equal(before.Count, (await EventsAsync()).Count);
        // E não desvincula: nenhum logout foi pedido.
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Method == HttpMethod.Delete);
    }

    // Número desconectado também pode ser reiniciado: é justamente o caso de
    // instância travada em `connecting`.
    [Fact]
    public async Task Restart_WorksOnADisconnectedNumber()
    {
        await SeedAsync(NumberStatus.Disconnected);
        FakeEvolution.When(HttpMethod.Post, "/instance/restart/", "{}");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(NumberStatus.Disconnected, (await NumberAsync()).Status);
    }

    // Número que nunca pareou não tem sessão para reiniciar — o caminho dele é
    // conectar pela primeira vez.
    [Fact]
    public async Task Restart_OnANumberThatNeverPaired_Returns409()
    {
        await SeedAsync(NumberStatus.Disconnected, paired: false);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(FakeEvolution.Requests, r =>
            r.Path.StartsWith("/instance/restart/", StringComparison.OrdinalIgnoreCase));
    }

    // Evolution que não confirma o restart vira erro na tela: dizer que reiniciou
    // sem ter reiniciado faria o operador esperar por algo que não vem.
    [Fact]
    public async Task Restart_WhenEvolutionRefuses_Fails()
    {
        await SeedAsync();
        FakeEvolution.When(HttpMethod.Post, "/instance/restart/", "erro", HttpStatusCode.InternalServerError);

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/restart", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
