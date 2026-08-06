using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Proxies;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Proxies;

public class ProxyIntegrationTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000f1");

    private Task SyncAsync() =>
        Factory.Services.GetRequiredService<IProxySyncService>().RunOnceAsync();

    private Task<int> ApplyAsync() =>
        Factory.Services.GetRequiredService<IProxyApplier>().ProcessPendingAsync();

    private async Task SeedSellerAsync() =>
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow.AddDays(-30) });
            return Task.CompletedTask;
        });

    private void EvolutionOk()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", """{"qrcode":{"code":"QR"}}""");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Post, "/proxy/set/", "{}");
        FakeEvolution.When(HttpMethod.Post, "/instance/restart/", "{}");
        FakeEvolution.When(HttpMethod.Delete, "/instance/", "{}");
    }

    private static JsonElement Body(FakeEvolutionHandler fake, string path) =>
        JsonDocument.Parse(fake.Requests.Last(r => r.Path.StartsWith(path)).Body!).RootElement;

    // A sincronização traz os proxies do fornecedor e guarda credenciais e o
    // limite de dispositivos quando ele informa.
    [Fact]
    public async Task Sync_ImportsProxiesFromTheProvider()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2), ("def", "191.0.0.2", 8080, null));

        await SyncAsync();

        var proxies = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().OrderBy(p => p.ShortId).ToListAsync());
        Assert.Equal(2, proxies.Count);
        Assert.Equal("191.0.0.1", proxies[0].Host);
        Assert.Equal(2, proxies[0].DeviceLimit);
        // Sem `devices` na resposta, a capacidade cai no default de config.
        Assert.Null(proxies[1].DeviceLimit);
        Assert.Equal(2, proxies[1].CapacityOr(2));
    }

    // Proxy que sumiu do fornecedor vira Expired e NÃO é apagado: o histórico de
    // bans dele é o que justifica trocar de plano ou de fornecedor.
    [Fact]
    public async Task Sync_WhenProxyDisappears_MarksExpiredWithoutDeleting()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();

        FakeProxyBr.Reset();
        FakeProxyBr.WithProxies();
        await SyncAsync();

        var proxy = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().SingleAsync());
        Assert.Equal(ProxyStatus.Expired, proxy.Status);
    }

    // Credencial mudou no fornecedor (IP rotacionado): as atribuições vigentes
    // voltam para "não aplicadas", senão o número seguiria tentando sair por um
    // endereço que não existe mais.
    [Fact]
    public async Task Sync_WhenCredentialsChange_MarksAssignmentsForReapply()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();

        var proxyId = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().Select(p => p.Id).SingleAsync());
        var numberId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = numberId, SellerId = SellerId, Phone = "5511900001111",
                InstanceName = "mv-p1", Status = NumberStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(), WhatsappNumberId = numberId, ProxyId = proxyId,
                AssignedAt = DateTime.UtcNow, AppliedAt = DateTime.UtcNow, Reason = ProxyAssignmentReason.Auto,
            });
            return Task.CompletedTask;
        });

        FakeProxyBr.Reset();
        FakeProxyBr.WithProxies(("abc", "191.0.0.9", 8080, 2));
        await SyncAsync();

        var assignment = await InDbAsync(db => db.Set<NumberProxyAssignment>().AsNoTracking().SingleAsync());
        Assert.Null(assignment.AppliedAt);
    }

    // O número NASCE atrás do proxy: a instância é criada com os campos proxy*,
    // e não entra nele depois com um restart.
    [Fact]
    public async Task Pairing_CreatesTheInstanceBehindTheProxy()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();
        EvolutionOk();

        var response = await Client.PostAsync($"/api/v1/sellers/{SellerId}/pairings", null);

        response.EnsureSuccessStatusCode();
        var body = Body(FakeEvolution, "/instance/create");
        Assert.Equal("191.0.0.1", body.GetProperty("proxyHost").GetString());
        // Porta socks5 (8081 = porta + 1 no fake) e porta como STRING.
        Assert.Equal("8081", body.GetProperty("proxyPort").GetString());
        Assert.Equal("socks5", body.GetProperty("proxyProtocol").GetString());
    }

    // Sem proxy com vaga, o pareamento SEGUE e o número nasce sem proxy: travar
    // o operador porque acabou proxy é um jeito ruim de descobrir que acabou.
    [Fact]
    public async Task Pairing_WithoutAvailableProxy_ProceedsWithoutOne()
    {
        await SeedSellerAsync();
        EvolutionOk();

        var response = await Client.PostAsync($"/api/v1/sellers/{SellerId}/pairings", null);

        response.EnsureSuccessStatusCode();
        Assert.False(Body(FakeEvolution, "/instance/create").TryGetProperty("proxyHost", out _));
    }

    // A Evolution recusa o proxy (400 Invalid proxy): o proxy é marcado como
    // falho e o pareamento CONCLUI sem proxy, em vez de travar quem está com o
    // celular na mão.
    [Fact]
    public async Task Pairing_WhenEvolutionRejectsTheProxy_FallsBackWithoutIt()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();

        var attempts = 0;
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.WhenSequence(HttpMethod.Post, "/instance/create", () =>
            attempts++ == 0
                ? (HttpStatusCode.BadRequest, """{"message":"Invalid proxy"}""")
                : (HttpStatusCode.OK, """{"qrcode":{"code":"QR"}}"""));

        var response = await Client.PostAsync($"/api/v1/sellers/{SellerId}/pairings", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(2, attempts);
        var proxy = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().SingleAsync());
        Assert.Equal(ProxyStatus.Failed, proxy.Status);
    }

    // Aplicar um proxy num número CONECTADO grava e reinicia: o agent do Baileys
    // é fixado na criação do socket, então sem restart a sessão seguiria no IP
    // antigo. Número desconectado só grava.
    [Fact]
    public async Task Applier_RestartsOnlyConnectedNumbers()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();
        EvolutionOk();

        var proxyId = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().Select(p => p.Id).SingleAsync());
        await SeedAsync(db =>
        {
            foreach (var (suffix, status) in new[] { ("on", NumberStatus.Active), ("off", NumberStatus.Disconnected) })
            {
                var id = Guid.NewGuid();
                db.Add(new WhatsappNumber
                {
                    Id = id, SellerId = SellerId, Phone = $"551190000{suffix.Length}{suffix.Length}11",
                    InstanceName = $"mv-{suffix}", Status = status, CreatedAt = DateTime.UtcNow,
                });
                db.Add(new NumberProxyAssignment
                {
                    Id = Guid.NewGuid(), WhatsappNumberId = id, ProxyId = proxyId,
                    AssignedAt = DateTime.UtcNow, Reason = ProxyAssignmentReason.Manual,
                });
            }

            return Task.CompletedTask;
        });

        Assert.Equal(2, await ApplyAsync());

        Assert.Equal(2, FakeEvolution.Requests.Count(r => r.Path.StartsWith("/proxy/set/")));
        var restarts = FakeEvolution.Requests.Where(r => r.Path.StartsWith("/instance/restart/")).ToList();
        Assert.Single(restarts);
        Assert.Contains("mv-on", restarts[0].Path);
    }

    // Regressão: uma atribuição que falha é tentada UMA vez por passada. Repescar
    // na mesma passada queimaria as tentativas todas sem intervalo nenhum — o
    // mesmo bug que já apareceu no ContactShareSender e no WebhookProcessor.
    [Fact]
    public async Task Applier_OnFailure_TriesOncePerPass()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();
        FakeEvolution.When(HttpMethod.Post, "/proxy/set/", "erro", HttpStatusCode.InternalServerError);

        var proxyId = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().Select(p => p.Id).SingleAsync());
        var numberId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = numberId, SellerId = SellerId, Phone = "5511900002222",
                InstanceName = "mv-x", Status = NumberStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(), WhatsappNumberId = numberId, ProxyId = proxyId,
                AssignedAt = DateTime.UtcNow, Reason = ProxyAssignmentReason.Manual,
            });
            return Task.CompletedTask;
        });

        await ApplyAsync();

        var assignment = await InDbAsync(db => db.Set<NumberProxyAssignment>().AsNoTracking().SingleAsync());
        Assert.Equal(1, assignment.Attempts);
        Assert.Null(assignment.AppliedAt);
    }

    // Com o interruptor desligado, o número nasce SEM proxy mesmo havendo proxy
    // com vaga — e nenhuma sessão conectada é mexida.
    [Fact]
    public async Task WhenSwitchIsOff_NumbersAreBornWithoutProxy()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();
        EvolutionOk();

        var off = await Client.PutAsJsonAsync("/api/v1/proxies/settings", new { enabled = false });
        off.EnsureSuccessStatusCode();

        await Client.PostAsync($"/api/v1/sellers/{SellerId}/pairings", null);

        Assert.False(Body(FakeEvolution, "/instance/create").TryGetProperty("proxyHost", out _));
        Assert.Equal(0, await ApplyAsync());
    }

    // A tela lista proxies com números, vendedores distintos e bans do período —
    // e a senha do proxy NUNCA sai na resposta.
    [Fact]
    public async Task Overview_ReportsCountsAndNeverLeaksThePassword()
    {
        FakeProxyBr.WithProxies(("abc", "191.0.0.1", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();

        var proxyId = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().Select(p => p.Id).SingleAsync());
        var numberId = Guid.NewGuid();
        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = numberId, SellerId = SellerId, Phone = "5511900003333",
                InstanceName = "mv-y", Status = NumberStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(), WhatsappNumberId = numberId, ProxyId = proxyId,
                AssignedAt = DateTime.UtcNow.AddDays(-2), Reason = ProxyAssignmentReason.Auto,
            });
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = numberId, State = "close", StatusReason = 403,
                ResultingStatus = NumberStatus.BannedTemporary, OccurredAt = DateTime.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });

        var raw = await Client.GetStringAsync("/api/v1/proxies");

        Assert.DoesNotContain("p-abc", raw);
        var overview = JsonDocument.Parse(raw).RootElement;
        var proxy = overview.GetProperty("proxies").EnumerateArray().Single();
        Assert.Equal(1, proxy.GetProperty("numbersCount").GetInt32());
        Assert.Equal(1, proxy.GetProperty("sellersCount").GetInt32());
        Assert.Equal(1, proxy.GetProperty("bansCount").GetInt32());
        Assert.Equal(0, overview.GetProperty("numbersWithoutProxy").GetInt32());
    }

    // Ban ANTES da troca de proxy fica com o proxy antigo: o vínculo é
    // histórico, senão mover um número reescreveria o passado.
    [Fact]
    public async Task Overview_AttributesBansToTheProxyThatWasCurrentAtTheTime()
    {
        FakeProxyBr.WithProxies(("old", "191.0.0.1", 8080, 2), ("new", "191.0.0.2", 8080, 2));
        await SyncAsync();
        await SeedSellerAsync();

        var proxies = await InDbAsync(db => db.Set<Proxy>().AsNoTracking().OrderBy(p => p.ShortId).ToListAsync());
        var newProxy = proxies.First(p => p.ShortId == "new");
        var oldProxy = proxies.First(p => p.ShortId == "old");
        var numberId = Guid.NewGuid();
        var switchedAt = DateTime.UtcNow.AddDays(-5);

        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = numberId, SellerId = SellerId, Phone = "5511900004444",
                InstanceName = "mv-z", Status = NumberStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-20),
            });
            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(), WhatsappNumberId = numberId, ProxyId = oldProxy.Id,
                AssignedAt = DateTime.UtcNow.AddDays(-20), ReleasedAt = switchedAt,
                Reason = ProxyAssignmentReason.Auto,
            });
            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(), WhatsappNumberId = numberId, ProxyId = newProxy.Id,
                AssignedAt = switchedAt, Reason = ProxyAssignmentReason.Rebalance,
            });
            // Ban de ANTES da troca.
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = numberId, State = "close", StatusReason = 403,
                ResultingStatus = NumberStatus.BannedTemporary, OccurredAt = DateTime.UtcNow.AddDays(-10),
            });
            return Task.CompletedTask;
        });

        var overview = await Client.GetFromJsonAsync<JsonElement>("/api/v1/proxies?from=" +
            DateTime.UtcNow.AddDays(-30).ToString("O") + "&to=" + DateTime.UtcNow.ToString("O"));

        var byShortId = overview.GetProperty("proxies").EnumerateArray()
            .ToDictionary(p => p.GetProperty("shortId").GetString()!, p => p.GetProperty("bansCount").GetInt32());
        Assert.Equal(1, byShortId["old"]);
        Assert.Equal(0, byShortId["new"]);
    }
}
