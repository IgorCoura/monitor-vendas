using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// O número nunca é digitado: ele vem do `wuid` que a Evolution informa ao
// conectar. É o que impede o cadastro dizer um número e a sessão ser de outro.
public class PairingTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid AnaId = Guid.Parse("5e11e000-0000-0000-0000-0000000000c1");
    private static readonly Guid BrunoId = Guid.Parse("5e11e000-0000-0000-0000-0000000000c2");
    private const string Phone = "5511968608425";

    private async Task SeedSellersAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR","base64":"data:image/png;base64,AAA"}""");
        FakeEvolution.When(HttpMethod.Delete, "/instance/delete/", "{}");
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");

        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = AnaId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow });
            db.Add(new Seller { Id = BrunoId, Name = "Bruno", Active = true, CreatedAt = DateTime.UtcNow });
            return Task.CompletedTask;
        });
    }

    private async Task SeedNumberAsync(Guid sellerId, NumberStatus status, string instance = "mv-antiga", string phone = Phone)
    {
        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = Guid.NewGuid(),
                SellerId = sellerId,
                Phone = phone,
                InstanceName = instance,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
            });

            return Task.CompletedTask;
        });
    }

    private async Task<PairingSessionDto> StartAsync(Guid sellerId)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{sellerId}/pairings", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingSessionDto>())!;
    }

    // Simula o WhatsApp conectando: é o webhook que revela qual número é.
    private async Task ConnectAsync(string instanceName, string phone = Phone, string profile = "Igor")
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "connection.update",
            instance = instanceName,
            data = new
            {
                instance = instanceName,
                wuid = $"{phone}@s.whatsapp.net",
                profileName = profile,
                state = "open",
                statusReason = 200,
            },
        });

        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        await ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();
    }

    private async Task<PairingSessionDto> StatusAsync(Guid id) =>
        (await Client.GetFromJsonAsync<PairingSessionDto>($"/api/v1/pairings/{id}"))!;

    // Número livre: o cadastro nasce com o telefone que o WhatsApp informou, não
    // com o que alguém digitou.
    [Fact]
    public async Task Pairing_WithFreeNumber_CreatesTheNumberFromTheConnectedWhatsapp()
    {
        await SeedSellersAsync();
        var session = await StartAsync(AnaId);

        await ConnectAsync(SessionInstance(session.Id));

        var status = await StatusAsync(session.Id);
        Assert.Equal("Completed", status.Status);
        Assert.Equal(Phone, status.DetectedPhone);

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync());
        Assert.Equal(Phone, number.Phone);
        Assert.Equal(AnaId, number.SellerId);
        Assert.Equal(NumberStatus.Active, number.Status);
    }

    // O QR tem que vir também no polling da sessão, não só na resposta da criação:
    // é de lá que a tela o lê. (Regressão: `GET /pairings/{id}` devolvia `qr` nulo
    // e o diálogo ficava no spinner para sempre — o QR nunca aparecia.)
    [Fact]
    public async Task Status_WhileAwaitingScan_CarriesTheQrCode()
    {
        await SeedSellersAsync();
        var session = await StartAsync(AnaId);

        var status = await StatusAsync(session.Id);

        Assert.Equal("AwaitingScan", status.Status);
        Assert.Equal("QR", status.Qr!.Code);
        Assert.Equal("data:image/png;base64,AAA", status.Qr.Base64);
    }

    // A Evolution troca o QR a cada ~30 s e avisa por `QRCODE_UPDATED`. Servir o
    // primeiro para sempre deixaria na tela um código que o WhatsApp já recusa.
    [Fact]
    public async Task QrCodeUpdated_ReplacesWhatTheScreenShows()
    {
        await SeedSellersAsync();
        var session = await StartAsync(AnaId);
        var instance = SessionInstance(session.Id);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "qrcode.updated",
            instance,
            data = new { qrcode = new { code = "QR-NOVO", @base64 = "data:image/png;base64,BBB" } },
        });
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        await ProcessAsync();

        var status = await StatusAsync(session.Id);
        Assert.Equal("QR-NOVO", status.Qr!.Code);
        Assert.Equal("data:image/png;base64,BBB", status.Qr.Base64);
    }

    // QR que chega depois de a sessão terminar não ressuscita a tela: a instância
    // pode continuar emitindo enquanto a Evolution não a apaga.
    [Fact]
    public async Task QrCodeUpdated_AfterTheSessionEnded_IsIgnored()
    {
        await SeedSellersAsync();
        var session = await StartAsync(AnaId);
        var instance = SessionInstance(session.Id);
        await Client.PostAsJsonAsync($"/api/v1/pairings/{session.Id}/cancel", new { });

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "qrcode.updated",
            instance,
            data = new { code = "QR-TARDIO", @base64 = "data:image/png;base64,CCC" },
        });
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        await ProcessAsync();

        var stored = await InDbAsync(db => db.Set<PairingSession>().AsNoTracking().SingleAsync(s => s.Id == session.Id));
        Assert.Equal("QR", stored.QrCode);
    }

    // Duas pessoas pareando ao mesmo tempo criariam duas instâncias e uma sessão
    // órfã: o banco decide quem chegou primeiro.
    [Fact]
    public async Task Pairing_WhileAnotherIsRunning_Returns409()
    {
        await SeedSellersAsync();
        await StartAsync(AnaId);

        var second = await Client.PostAsJsonAsync($"/api/v1/sellers/{BrunoId}/pairings", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await InDbAsync(db => db.Set<PairingSession>().CountAsync()));
    }

    // Número já conectado em outro vendedor: a tentativa é descartada e a
    // instância recém-criada, apagada.
    [Fact]
    public async Task Pairing_WhenNumberIsAlreadyConnected_IsRejected()
    {
        await SeedSellersAsync();
        await SeedNumberAsync(BrunoId, NumberStatus.Active);

        var session = await StartAsync(AnaId);
        var instance = SessionInstance(session.Id);
        await ConnectAsync(instance);

        var status = await StatusAsync(session.Id);
        Assert.Equal("Rejected", status.Status);
        Assert.Contains("Bruno", status.Error);
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.Contains(instance, StringComparison.OrdinalIgnoreCase));
    }

    // Mesmo vendedor, número desconectado: nada é apagado — só o aviso de que o
    // número já existe e o caminho certo é reconectar por ele.
    [Fact]
    public async Task Pairing_WhenNumberExistsForTheSameSeller_OnlyWarns()
    {
        await SeedSellersAsync();
        await SeedNumberAsync(AnaId, NumberStatus.Disconnected);

        var session = await StartAsync(AnaId);
        await ConnectAsync(SessionInstance(session.Id));

        var status = await StatusAsync(session.Id);
        Assert.Equal("Rejected", status.Status);
        Assert.Contains("já está cadastrado", status.Error);

        // O cadastro antigo continua intacto, com a instância dele.
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync());
        Assert.Equal("mv-antiga", number.InstanceName);
    }

    // Outro vendedor, desconectado: exige confirmação de transferência. O
    // registro é reaproveitado — criar outro deixaria o histórico órfão.
    [Fact]
    public async Task Pairing_WhenNumberBelongsToAnotherSeller_AsksToTransfer()
    {
        await SeedSellersAsync();
        await SeedNumberAsync(BrunoId, NumberStatus.Disconnected);

        var session = await StartAsync(AnaId);
        var instance = SessionInstance(session.Id);
        await ConnectAsync(instance);

        var pending = await StatusAsync(session.Id);
        Assert.Equal("AwaitingConfirmation", pending.Status);
        Assert.True(pending.RequiresTransfer);
        Assert.Equal("Bruno", pending.CurrentOwnerName);

        var confirmed = await Client.PostAsJsonAsync($"/api/v1/pairings/{session.Id}/confirm", new { });
        confirmed.EnsureSuccessStatusCode();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync());
        Assert.Equal(AnaId, number.SellerId);
        Assert.Equal(instance, number.InstanceName);
        Assert.Equal(NumberStatus.Active, number.Status);
    }

    // Número banido em outro vendedor: além da transferência, a tela precisa
    // avisar do ban — reativar por engano apagaria a decisão do operador.
    [Fact]
    public async Task Pairing_WhenNumberIsBanned_AsksForBothConfirmations()
    {
        await SeedSellersAsync();
        await SeedNumberAsync(BrunoId, NumberStatus.BannedPermanent);

        var session = await StartAsync(AnaId);
        await ConnectAsync(SessionInstance(session.Id));

        var pending = await StatusAsync(session.Id);
        Assert.Equal("AwaitingConfirmation", pending.Status);
        Assert.True(pending.RequiresTransfer);
        Assert.True(pending.RequiresBannedConfirmation);

        await Client.PostAsJsonAsync($"/api/v1/pairings/{session.Id}/confirm", new { });

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync());
        Assert.Equal(NumberStatus.Active, number.Status);
        Assert.Equal(AnaId, number.SellerId);
    }

    // Recusar a transferência não mexe em nada: o cadastro antigo fica como
    // estava e a instância da tentativa é apagada.
    [Fact]
    public async Task Pairing_WhenTransferIsRefused_KeepsEverythingAsItWas()
    {
        await SeedSellersAsync();
        await SeedNumberAsync(BrunoId, NumberStatus.Disconnected);

        var session = await StartAsync(AnaId);
        await ConnectAsync(SessionInstance(session.Id));
        await Client.PostAsJsonAsync($"/api/v1/pairings/{session.Id}/cancel", new { });

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync());
        Assert.Equal(BrunoId, number.SellerId);
        Assert.Equal("mv-antiga", number.InstanceName);
        Assert.Equal(NumberStatus.Disconnected, number.Status);
    }

    // Enquanto a sessão não é confirmada, o despejo de histórico que o WhatsApp
    // faz ao conectar não pode virar dado: não sabemos de quem é o número.
    [Fact]
    public async Task Pairing_DiscardsMessagesReceivedBeforeConfirmation()
    {
        await SeedSellersAsync();
        var session = await StartAsync(AnaId);
        var instance = SessionInstance(session.Id);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "messages.upsert",
            instance,
            data = new
            {
                key = new { remoteJid = "5511911112222@s.whatsapp.net", fromMe = false, id = "QUAR-1" },
                pushName = "Cliente",
                message = new { conversation = "oi" },
                messageType = "conversation",
                messageTimestamp = 1785000000,
            },
        });

        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Api.Features.Conversations.Message>().CountAsync()));
    }

    private string SessionInstance(Guid sessionId) =>
        InDbAsync(db => db.Set<PairingSession>().Where(s => s.Id == sessionId).Select(s => s.InstanceName).SingleAsync())
            .GetAwaiter().GetResult();
}
