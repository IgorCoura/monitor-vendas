using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Contacts;

public class ContactShareTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private const string Instance = "mv-5511900005555";
    private const string Destination = "5511777770000";
    private const string Period = "from=2026-07-01T00:00:00Z&to=2026-07-31T00:00:00Z";

    private Guid _numberId;

    private static long Ts(int day, int hourUtc, int minute = 0) =>
        new DateTimeOffset(new DateTime(2026, 7, day, hourUtc, minute, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task SeedAsync()
    {
        FakeEvolution.When(HttpMethod.Post, "/instance/create", "{}");
        FakeEvolution.When(HttpMethod.Post, "/webhook/set/", "{}");
        FakeEvolution.When(HttpMethod.Get, "/instance/connect/", """{"code":"QR"}""");
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"key":{"id":"SHARE-1"}}""");

        var seller = await (await Client.PostAsJsonAsync("/api/v1/sellers", new { name = "Ana" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var number = await (await Client.PostAsJsonAsync(
                $"/api/v1/sellers/{seller.GetProperty("id").GetGuid()}/numbers", new { phone = "5511900005555" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        _numberId = number.GetProperty("number").GetProperty("id").GetGuid();

        await PostWebhookAsync($$"""
            { "event": "connection.update", "instance": "{{Instance}}",
              "data": { "state": "open", "statusReason": 200 }, "date_time": "2026-06-30T12:00:00Z" }
            """);

        await AddContactAsync("M1", "5511700001111@s.whatsapp.net", "Maria Silva", Ts(1, 13));
        await AddContactAsync("J1", "5511700002222@s.whatsapp.net", "João Souza", Ts(2, 13));
        await ProcessWebhooksAsync();
    }

    private async Task AddContactAsync(string id, string jid, string? pushName, long ts) =>
        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{jid}}", "fromMe": false, "id": "{{id}}" },
                "message": { "conversation": "oi" },
                "messageType": "conversation",
                "pushName": {{(pushName is null ? "null" : $"\"{pushName}\"")}},
                "messageTimestamp": {{ts}}
              }
            }
            """);

    private async Task PostWebhookAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task ProcessWebhooksAsync() =>
        await Factory.Services.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();

    private Task<HttpResponseMessage> ShareAsync(string extraQuery = "") =>
        Client.PostAsJsonAsync($"/api/v1/contacts/share?{Period}{extraQuery}",
            new { senderNumberId = _numberId, destination = Destination });

    // Deixa o número remetente restringido pelo WhatsApp (estado que o 463 produz).
    private Task PauseSenderAsync() =>
        InDbAsync(async db =>
        {
            await db.Set<MonitorVendas.Api.Features.Numbers.WhatsappNumber>()
                .Where(n => n.Id == _numberId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.SendingPausedUntil, DateTime.UtcNow.AddHours(6))
                    .SetProperty(n => n.SendingPauseReason, "O WhatsApp restringiu o envio deste número."));
            return 0;
        });

    // O corpo trafega como JSON (acento vira ã): compara-se o texto decodificado.
    private static JsonElement LastSend(FakeEvolutionHandler fake) =>
        JsonDocument.Parse(fake.Requests.Last(r => r.Path.StartsWith("/message/sendText/")).Body!).RootElement;

    private static string SentText(FakeEvolutionHandler fake) =>
        LastSend(fake).GetProperty("text").GetString()!;

    // Pedir o envio monta e enfileira as mensagens na hora, devolvendo as contagens
    // (nada é enviado ainda: quem manda é o serviço em background).
    [Fact]
    public async Task Share_QueuesMessagesAndReportsCounts()
    {
        await SeedAsync();

        var response = await ShareAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var share = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, share.GetProperty("totalContacts").GetInt32());
        Assert.Equal(1, share.GetProperty("totalMessages").GetInt32());
        Assert.Equal(0, share.GetProperty("sentMessages").GetInt32());
        Assert.Equal("Pending", share.GetProperty("status").GetString());
        Assert.Equal(Destination, share.GetProperty("destination").GetString());
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/message/sendText/"));
    }

    // O serviço envia todas as mensagens da fila, no formato "Nome - número", e
    // fecha o envio como concluído.
    [Fact]
    public async Task Sender_SendsEverythingAndCompletes()
    {
        await SeedAsync();
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.Equal(Destination, LastSend(FakeEvolution).GetProperty("number").GetString());
        var body = SentText(FakeEvolution);
        Assert.Contains("Maria Silva - 5511700001111", body);
        Assert.Contains("João Souza - 5511700002222", body);

        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Completed", status.GetProperty("status").GetString());
        Assert.Equal(1, status.GetProperty("sentMessages").GetInt32());
    }

    // O conteúdo é congelado no pedido: contato que entra depois não aparece na
    // mensagem — senão o enviado seria diferente do que foi confirmado na tela.
    [Fact]
    public async Task Share_SnapshotIsFrozenAtRequestTime()
    {
        await SeedAsync();
        await ShareAsync();

        await AddContactAsync("C1", "5511700003333@s.whatsapp.net", "Carla Dias", Ts(3, 13));
        await ProcessWebhooksAsync();
        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.DoesNotContain("Carla Dias", SentText(FakeEvolution));
    }

    // A mensagem que nós mesmos enviamos volta pelo webhook como fromMe e NÃO pode
    // ser contada como mensagem enviada pelo vendedor (regressão: inflava a métrica).
    [Fact]
    public async Task OwnShareMessage_IsNotCountedAsSellerMessage()
    {
        await SeedAsync();
        await ShareAsync();
        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        await PostWebhookAsync($$"""
            {
              "event": "messages.upsert",
              "instance": "{{Instance}}",
              "data": {
                "key": { "remoteJid": "{{Destination}}@s.whatsapp.net", "fromMe": true, "id": "SHARE-1" },
                "message": { "conversation": "Contatos" },
                "messageType": "conversation",
                "messageTimestamp": {{Ts(5, 13)}}
              }
            }
            """);
        await ProcessWebhooksAsync();

        Assert.False(await InDbAsync(db => db.Set<Message>().AnyAsync(m => m.WaMessageId == "SHARE-1")));
        var ranking = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/ranking?{Period}");
        Assert.Equal(0, ranking.EnumerateArray().First().GetProperty("metrics").GetProperty("messagesSent").GetInt32());
    }

    // Falha na Evolution registra a tentativa e mantém o envio pendente: a próxima
    // passada retoma de onde parou, sem duplicar o que já saiu.
    // Regressão: o laço repescava o mesmo envio dentro da mesma passada e gastava as
    // 5 tentativas de uma vez, marcando como falho na primeira indisponibilidade.
    [Fact]
    public async Task Sender_WhenEvolutionFails_KeepsPendingAndCountsAttempt()
    {
        await SeedAsync();
        FakeEvolution.Reset();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", """{"error":"x"}""", HttpStatusCode.InternalServerError);
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Pending", status.GetProperty("status").GetString());
        Assert.Equal(0, status.GetProperty("sentMessages").GetInt32());
        Assert.Equal(1, await InDbAsync(db => db.Set<ContactShareMessage>()
            .Where(m => m.ContactShareId == id).MaxAsync(m => m.Attempts)));
    }

    // Erro 463 (limite de contato frio) no meio do envio encerra ESTE envio como
    // falho, com o motivo visível, e pausa o número — sem gastar tentativa.
    // Deixá-lo pendente o faria voltar sozinho sem ninguém decidir.
    [Fact]
    public async Task Sender_OnReachoutRestriction_FailsTheShareAndPausesTheNumber()
    {
        await SeedAsync();
        FakeEvolution.Reset();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/",
            """{"status":500,"error":{"code":463,"message":"NackCallerReachoutTimelocked"}}""",
            HttpStatusCode.InternalServerError);
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.Contains("restringiu", status.GetProperty("error").GetString());
        var number = await InDbAsync(db => db.Set<MonitorVendas.Api.Features.Numbers.WhatsappNumber>().SingleAsync());
        Assert.NotNull(number.SendingPausedUntil);
        Assert.True(number.SendingPausedUntil > DateTime.UtcNow.AddHours(1));
        Assert.Equal(0, await InDbAsync(db => db.Set<ContactShareMessage>()
            .Where(m => m.ContactShareId == id).MaxAsync(m => m.Attempts)));
    }

    // Número restringido: pedir um envio novo devolve o AVISO com o motivo, sem
    // criar nada — o operador precisa ver o risco antes de decidir.
    [Fact]
    public async Task Share_WhenNumberIsRestricted_WarnsInsteadOfSending()
    {
        await SeedAsync();
        await PauseSenderAsync();

        var response = await ShareAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("code").GetString() == "sendingPaused");
        Assert.Equal(0, await InDbAsync(db => db.Set<ContactShare>().CountAsync()));
    }

    // Confirmado o aviso, o envio acontece mesmo com o número pausado: a proteção
    // é conselho, não trava — quem decide é quem opera.
    [Fact]
    public async Task Share_WithConfirmedRisk_SendsEvenWhilePaused()
    {
        await SeedAsync();
        await PauseSenderAsync();

        var response = await ShareAsync("&confirmRisk=true");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await Factory.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.Contains(FakeEvolution.Requests, r => r.Path.StartsWith("/message/sendText/"));
    }

    // Fora do expediente o envio comum espera a janela útil (não é recusa, é
    // agendamento); o confirmado pelo operador sai na hora. A janela de zero
    // horas fecha o calendário em qualquer dia/hora em que a suíte rode.
    [Fact]
    public async Task Sender_OutsideBusinessHours_HoldsNormalShareButSendsAcknowledgedOne()
    {
        await SeedAsync();
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        using var host = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ContactShare:BusinessHoursOnly", "true");
            b.UseSetting("Metrics:BusinessDayStartHour", "9");
            b.UseSetting("Metrics:BusinessDayEndHour", "9");
            b.UseSetting("Metrics:SaturdayEnabled", "false");
        });
        await host.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.StartsWith("/message/sendText/"));
        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Pending", status.GetProperty("status").GetString());

        // O mesmo envio, agora marcado como confirmado pelo operador, sai.
        await InDbAsync(async db =>
        {
            await db.Set<ContactShare>().Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RiskAcknowledged, true));
            return 0;
        });
        await host.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        Assert.Contains(FakeEvolution.Requests, r => r.Path.StartsWith("/message/sendText/"));
    }

    // Esgotadas as tentativas, o envio é marcado como falho com o motivo.
    [Fact]
    public async Task Sender_WhenAttemptsRunOut_MarksFailed()
    {
        await SeedAsync();
        FakeEvolution.Reset();
        FakeEvolution.When(HttpMethod.Post, "/message/sendText/", "{}", HttpStatusCode.InternalServerError);
        var share = await (await ShareAsync()).Content.ReadFromJsonAsync<JsonElement>();
        var id = share.GetProperty("id").GetGuid();

        using var host = Factory.WithWebHostBuilder(b => b.UseSetting("ContactShare:MaxAttempts", "1"));
        await host.Services.GetRequiredService<IContactShareSender>().ProcessPendingAsync();

        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/contacts/share/{id}");
        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.NotNull(status.GetProperty("error").GetString());
    }

    // Lista grande é recusada com orientação, em vez de disparar dezenas de
    // mensagens e queimar o número.
    [Fact]
    public async Task Share_WhenListExceedsMessageCap_IsRejected()
    {
        await SeedAsync();

        using var host = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ContactShare:MaxCharsPerMessage", "80");
            b.UseSetting("ContactShare:MaxMessagesPerShare", "1");
        });

        var response = await host.CreateClient().PostAsJsonAsync(
            $"/api/v1/contacts/share?{Period}",
            new { senderNumberId = _numberId, destination = Destination });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Aperte o filtro", body.GetProperty("error").GetString());
    }

    // Destino sem DDI/DDD é recusado antes de qualquer envio.
    [Fact]
    public async Task Share_InvalidDestination_IsRejected()
    {
        await SeedAsync();

        var response = await Client.PostAsJsonAsync($"/api/v1/contacts/share?{Period}",
            new { senderNumberId = _numberId, destination = "9999" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Número remetente desconectado ou banido não pode enviar.
    [Fact]
    public async Task Share_InactiveSender_IsRejected()
    {
        await SeedAsync();
        await Client.PostAsync($"/api/v1/numbers/{_numberId}/ban-permanent", null);

        var response = await ShareAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("não está conectado", body.GetProperty("error").GetString());
    }

    // Sem contatos no filtro não há o que enviar.
    [Fact]
    public async Task Share_WithoutContacts_IsRejected()
    {
        await SeedAsync();

        var response = await Client.PostAsJsonAsync(
            "/api/v1/contacts/share?from=2026-09-01T00:00:00Z&to=2026-09-30T00:00:00Z",
            new { senderNumberId = _numberId, destination = Destination });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // A lista global de números traz o vendedor dono — é o que alimenta a escolha
    // do remetente na tela de contatos.
    [Fact]
    public async Task Numbers_ListAll_IncludesSellerName()
    {
        await SeedAsync();

        var numbers = await Client.GetFromJsonAsync<JsonElement>("/api/v1/numbers");

        var number = Assert.Single(numbers.EnumerateArray().ToList());
        Assert.Equal("5511900005555", number.GetProperty("phone").GetString());
        Assert.Equal("Ana", number.GetProperty("sellerName").GetString());
        Assert.Equal("Active", number.GetProperty("status").GetString());
    }

    // Envio inexistente devolve 404.
    [Fact]
    public async Task Status_UnknownShare_ReturnsNotFound()
    {
        var response = await Client.GetAsync($"/api/v1/contacts/share/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
