using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// O ciclo de vida da sessão de pareamento: o que acontece quando ela não termina
// bem. Vaga única presa é o pior desfecho — ninguém mais conecta WhatsApp no
// sistema até alguém mexer no banco.
public class PairingLifecycleTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid AnaId = Guid.Parse("5e11e000-0000-0000-0000-0000000000e1");
    private const string Phone = "5511968608425";

    private async Task SeedSellerAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR","base64":"data:image/png;base64,AAA"}""");
        FakeEvolution.When(HttpMethod.Delete, "/instance/delete/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");

        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = AnaId, Name = "Ana", Active = true, CreatedAt = DateTime.UtcNow });
            return Task.CompletedTask;
        });
    }

    private async Task<PairingSessionDto> StartAsync()
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{AnaId}/pairings", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingSessionDto>())!;
    }

    private Task<PairingSession> SessionAsync(Guid id) =>
        InDbAsync(db => db.Set<PairingSession>().AsNoTracking().SingleAsync(s => s.Id == id));

    private async Task ConnectAsync(string instanceName, string phone = Phone)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "connection.update",
            instance = instanceName,
            data = new { instance = instanceName, wuid = $"{phone}@s.whatsapp.net", state = "open", statusReason = 200 },
        });

        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();
    }

    // Vendedor que não existe é 404, não 502: erro de quem chamou não pode ser
    // apresentado como Evolution fora do ar. (Regressão: devolvia 502.)
    [Fact]
    public async Task Start_ForAnUnknownSeller_Returns404AndKeepsTheSlotFree()
    {
        await SeedSellerAsync();

        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{Guid.NewGuid()}/pairings", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Set<PairingSession>().CountAsync()));
    }

    // Evolution fora do ar na criação da instância: sem instância não há QR, então
    // a sessão morre na hora e a vaga fica livre para a próxima tentativa.
    [Fact]
    public async Task Start_WhenEvolutionIsDown_FreesTheSlot()
    {
        await SeedSellerAsync();
        FakeEvolution.Reset();
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "erro", HttpStatusCode.InternalServerError);
        FakeEvolution.When(HttpMethod.Delete, "/instance/delete/", "{}");

        var response = await Client.PostAsJsonAsync($"/api/v1/sellers/{AnaId}/pairings", new { });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var session = await InDbAsync(db => db.Set<PairingSession>().AsNoTracking().SingleAsync());
        Assert.Equal(PairingStatus.Rejected, session.Status);
        Assert.Null(session.Active);
    }

    // QR expirado e esquecido não pode segurar a vaga única: a faxina encerra a
    // sessão vencida e apaga a instância que ficou pendurada na Evolution.
    [Fact]
    public async Task Cleanup_EndsExpiredSessionsAndDeletesTheirInstances()
    {
        await SeedSellerAsync();
        var session = await StartAsync();

        // Empurra o vencimento para trás, como se o QR tivesse ficado aberto.
        await SeedAsync(async db =>
        {
            var stored = await db.Set<PairingSession>().SingleAsync(s => s.Id == session.Id);
            stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        });

        var cleanup = new PairingCleanupService(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<ILogger<PairingCleanupService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await cleanup.StartAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested && (await SessionAsync(session.Id)).Active is not null)
                await Task.Delay(50, cts.Token);
        }
        finally
        {
            await cleanup.StopAsync(CancellationToken.None);
        }

        var expired = await SessionAsync(session.Id);
        Assert.Equal(PairingStatus.Rejected, expired.Status);
        Assert.Contains("QR code", expired.Error);
        Assert.Contains(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.Contains(expired.InstanceName, StringComparison.OrdinalIgnoreCase));

        // Com a vaga liberada, a próxima tentativa entra normalmente.
        var next = await Client.PostAsJsonAsync($"/api/v1/sellers/{AnaId}/pairings", new { });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    // Confirmar uma sessão que não está esperando confirmação é 409: só existe
    // decisão humana a tomar em transferência ou reativação de banido.
    [Fact]
    public async Task Confirm_OnASessionThatIsNotWaiting_Returns409()
    {
        await SeedSellerAsync();
        var session = await StartAsync();

        var response = await Client.PostAsJsonAsync($"/api/v1/pairings/{session.Id}/confirm", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Sessão inexistente é 404 em todas as rotas — id velho na tela não pode virar
    // erro 500.
    [Fact]
    public async Task UnknownSession_Is404Everywhere()
    {
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/pairings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/pairings/{id}/confirm", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/pairings/{id}/cancel", new { })).StatusCode);
    }

    // A Evolution conectou mas não disse qual número é: sem `wuid` não há cadastro
    // possível — inventar o número era exatamente o bug que o pareamento resolve.
    [Fact]
    public async Task Connect_WithoutTellingTheNumber_RejectsTheSession()
    {
        await SeedSellerAsync();
        var session = await StartAsync();
        var instance = (await SessionAsync(session.Id)).InstanceName;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "connection.update",
            instance,
            data = new { instance, state = "open", statusReason = 200 },
        });
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();

        var stored = await SessionAsync(session.Id);
        Assert.Equal(PairingStatus.Rejected, stored.Status);
        Assert.Contains("não informou", stored.Error);
        Assert.Equal(0, await InDbAsync(db => db.Set<WhatsappNumber>().CountAsync()));
    }

    // A promessa da quarentena: o que foi descartado enquanto a sessão não era
    // confiável volta assim que o número tem dono. (Regressão: o evento cru ficava
    // marcado como processado e o dedupe por `key.id` impedia a reconciliação de
    // sintetizá-lo de novo — o descarte era definitivo.)
    [Fact]
    public async Task AfterPairing_WhatQuarantineDiscardedIsReprocessed()
    {
        await SeedSellerAsync();
        var session = await StartAsync();
        var instance = (await SessionAsync(session.Id)).InstanceName;

        var discarded = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "messages.upsert",
            instance,
            data = new
            {
                key = new { remoteJid = "5511955554444@s.whatsapp.net", fromMe = false, id = "QUAR-9" },
                pushName = "Cliente",
                message = new { conversation = "cheguei durante a quarentena" },
                messageType = "conversation",
                messageTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            },
        });
        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(discarded, System.Text.Encoding.UTF8, "application/json"));
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync()));

        await ConnectAsync(instance);

        // A marca d'água volta para o início da tentativa: o que nem chegou por
        // webhook ainda pode vir pela varredura.
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync());
        var quarantineFrom = (await SessionAsync(session.Id)).QuarantineFrom;
        Assert.Equal(quarantineFrom, number.LastReconciledAt);

        // E o evento descartado volta para a fila, sem depender da Evolution.
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();

        var recovered = await InDbAsync(db => db.Set<Message>().AsNoTracking().SingleAsync());
        Assert.Equal("QUAR-9", recovered.WaMessageId);
        Assert.Equal(number.Id, recovered.WhatsappNumberId);

        // Reprocessar não pode duplicar: o pipeline é idempotente por `key.id`.
        await Factory.Services.GetRequiredService<IReconciliationService>().RunOnceAsync();
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync()));
    }
}
