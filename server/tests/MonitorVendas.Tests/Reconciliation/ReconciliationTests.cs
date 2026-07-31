using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Reconciliation;

public class ReconciliationTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511999992222";
    private Guid _numberId;

    // `paired: true` (default) representa o caso normal: número que já conectou
    // alguma vez. A reconciliação só varre esses — número que nunca pareou não
    // tem instância viva na Evolution para consultar.
    private async Task SeedNumberAsync(bool paired = true)
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Vendedor" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var created = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511999992222" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        _numberId = created.GetProperty("number").GetProperty("id").GetGuid();

        if (!paired)
            return;

        // Um evento de conexão bem-sucedida no histórico: é ele que distingue
        // "nunca pareou" de "pareou e caiu".
        await SeedAsync(db =>
        {
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = _numberId,
                State = "open",
                ResultingStatus = NumberStatus.Active,
                OccurredAt = DateTime.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });
    }

    private async Task<int> RunReconciliationAsync() =>
        await Factory.Services.GetRequiredService<IReconciliationService>().RunOnceAsync();

    private async Task ProcessAsync() =>
        await Factory.Services.GetRequiredService<MonitorVendas.Api.Features.Webhooks.IWebhookProcessor>()
            .ProcessPendingAsync();

    // Mensagem que existe na Evolution mas não no banco (webhook perdido) é recuperada
    // pela reconciliação e persiste uma única vez, mesmo rodando o job duas vezes.
    [Fact]
    public async Task MissedMessage_IsRecoveredExactlyOnce()
    {
        await SeedNumberAsync();
        var recentTs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        // Estado "close" casa com o status Disconnected do número recém-criado:
        // o teste mede apenas a recuperação da mensagem, sem evento de estado junto.
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": {
                "records": [
                  {
                    "key": { "remoteJid": "5511777776666@s.whatsapp.net", "fromMe": false, "id": "LOST-1" },
                    "pushName": "Cliente Perdido",
                    "message": { "conversation": "webhook caiu" },
                    "messageType": "conversation",
                    "messageTimestamp": {{recentTs}}
                  }
                ]
              }
            }
            """);

        var first = await RunReconciliationAsync();
        await ProcessAsync();
        var second = await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var messages = await InDbAsync(db => db.Set<Message>().Where(m => m.WaMessageId == "LOST-1").CountAsync());
        Assert.Equal(1, messages);
    }

    // Mensagem fora da janela de lookback (antiga) não é reimportada — sem backfill.
    [Fact]
    public async Task OldMessage_OutsideLookback_IsIgnored()
    {
        await SeedNumberAsync();
        var oldTs = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": { "records": [ { "key": { "remoteJid": "x@s.whatsapp.net", "fromMe": false, "id": "OLD-1" },
                "message": { "conversation": "antiga" }, "messageType": "conversation", "messageTimestamp": {{oldTs}} } ] }
            }
            """);

        await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync(m => m.WaMessageId == "OLD-1")));
    }

    // Estado real "open" na Evolution com número marcado Disconnected → reconciliação reativa via pipeline.
    [Fact]
    public async Task StateMismatch_IsResynchronized()
    {
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"open"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");

        await RunReconciliationAsync();
        await ProcessAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Equal(NumberStatus.Active, number.Status);
    }

    // Número cadastrado que nunca pareou não tem instância na Evolution: a
    // reconciliação nem consulta, para não encher o log de 404 a cada ciclo.
    [Fact]
    public async Task NumberThatNeverPaired_IsSkipped()
    {
        await SeedNumberAsync(paired: false);
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", "{}", HttpStatusCode.NotFound);

        await RunReconciliationAsync();

        Assert.DoesNotContain(
            FakeEvolution.Requests,
            r => r.Path.StartsWith("/instance/connectionState/", StringComparison.OrdinalIgnoreCase));

        // Sem consulta, sem marca: o número segue esperando o primeiro pareamento.
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.Null(number.LastReconciledAt);
    }

    // Número que pareou e caiu continua sendo reconciliado: é justamente o caso
    // em que mensagens podem ter se perdido.
    [Fact]
    public async Task NumberThatPairedAndDropped_IsStillReconciled()
    {
        // O número está Disconnected, mas já pareou um dia: precisa ser varrido,
        // porque é aí que mensagens podem ter se perdido.
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");

        await RunReconciliationAsync();

        Assert.Contains(
            FakeEvolution.Requests,
            r => r.Path.StartsWith("/instance/connectionState/", StringComparison.OrdinalIgnoreCase));
    }

    // Ciclo bem-sucedido deixa a marca d'água: é ela que diz de onde varrer na
    // próxima vez, no lugar da janela fixa.
    [Fact]
    public async Task SuccessfulRun_MovesTheWatermark()
    {
        await SeedNumberAsync();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", """{"messages":{"records":[]}}""");
        var before = DateTime.UtcNow;

        await RunReconciliationAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.NotNull(number.LastReconciledAt);
        Assert.InRange(number.LastReconciledAt!.Value, before.AddSeconds(-5), DateTime.UtcNow);
    }

    // Queda longa da API: mensagem de 6h atrás está fora da janela de 2h, mas
    // dentro da marca d'água — e por isso é recuperada. Era exatamente o buraco
    // da janela fixa: parada maior que ela perdia dados em silêncio.
    [Fact]
    public async Task DowntimeLongerThanTheWindow_IsStillRecovered()
    {
        await SeedNumberAsync();
        // A marca diz que a última varredura foi há 8 horas.
        await SeedAsync(db =>
        {
            db.Set<WhatsappNumber>().Single().LastReconciledAt = DateTime.UtcNow.AddHours(-8);
            return Task.CompletedTask;
        });

        var duringDowntime = DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeSeconds();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": { "records": [ { "key": { "remoteJid": "5511777776666@s.whatsapp.net", "fromMe": false, "id": "DOWN-1" },
                "message": { "conversation": "chegou durante a queda" }, "messageType": "conversation", "messageTimestamp": {{duringDowntime}} } ] }
            }
            """);

        await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(1, await InDbAsync(db => db.Set<Message>().CountAsync(m => m.WaMessageId == "DOWN-1")));
    }

    // O teto continua valendo: número parado há semanas não puxa o histórico
    // inteiro da Evolution de uma vez só.
    [Fact]
    public async Task WatermarkOlderThanTheCeiling_IsClamped()
    {
        await SeedNumberAsync();
        await SeedAsync(db =>
        {
            db.Set<WhatsappNumber>().Single().LastReconciledAt = DateTime.UtcNow.AddDays(-30);
            return Task.CompletedTask;
        });

        // Dentro dos 30 dias da marca, mas muito além do teto de 72h.
        var ancient = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", """{"instance":{"state":"close"}}""");
        FakeEvolution.When(HttpMethod.Post, "/chat/findMessages/", $$"""
            {
              "messages": { "records": [ { "key": { "remoteJid": "x@s.whatsapp.net", "fromMe": false, "id": "ANCIENT-1" },
                "message": { "conversation": "antiga demais" }, "messageType": "conversation", "messageTimestamp": {{ancient}} } ] }
            }
            """);

        await RunReconciliationAsync();
        await ProcessAsync();

        Assert.Equal(0, await InDbAsync(db => db.Set<Message>().CountAsync(m => m.WaMessageId == "ANCIENT-1")));
    }

    // Evolution fora do ar não move a marca: avançá-la declararia varrido um
    // trecho que ninguém leu, e o buraco ficaria permanente.
    [Fact]
    public async Task WhenEvolutionIsDown_TheWatermarkDoesNotMove()
    {
        await SeedNumberAsync();
        var watermark = DateTime.UtcNow.AddHours(-5);
        await SeedAsync(db =>
        {
            db.Set<WhatsappNumber>().Single().LastReconciledAt = watermark;
            return Task.CompletedTask;
        });

        FakeEvolution.When(HttpMethod.Get, "/instance/connectionState/", "{}", HttpStatusCode.InternalServerError);

        await RunReconciliationAsync();

        var number = await InDbAsync(db => db.Set<WhatsappNumber>().SingleAsync(n => n.Id == _numberId));
        Assert.True(
            (number.LastReconciledAt!.Value - watermark).Duration() < TimeSpan.FromSeconds(1),
            $"A marca deveria continuar em {watermark:O}, mas está em {number.LastReconciledAt:O}.");
    }
}
