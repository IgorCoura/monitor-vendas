using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Numbers;

// statusReason do Baileys: 401 = logout (aparelho desvinculado, QR novo, sem
// punição) · 403 = ban · 428 = conexão fechada (reconectar é normal) · 515 =
// pede restart. Ban começa como temporário; a promoção a permanente é decisão
// manual (POST /numbers/{id}/ban-permanent).
public sealed class ConnectionUpdateHandler(
    IDirtyDayTracker dirtyDays,
    PairingService pairing,
    IOptions<AntiBanOptions> antiBan,
    ILogger<ConnectionUpdateHandler> logger) : IWebhookEventHandler
{
    public string EventType => "CONNECTION_UPDATE";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data)
            return;

        var state = WebhookPayload.GetString(data, "state")?.ToLowerInvariant();
        if (state is not ("open" or "close"))
            return;

        // Instância de uma tentativa de pareamento: é aqui que descobrimos QUAL
        // WhatsApp conectou, e só depois disso ele vira cadastro.
        var session = await db.Set<PairingSession>()
            .FirstOrDefaultAsync(s => s.InstanceName == evt.InstanceName && s.Active == true, ct);

        if (session is not null)
        {
            if (state == "open")
            {
                await pairing.ResolveConnectedAsync(
                    session,
                    WebhookPayload.GetString(data, "wuid") ?? WebhookPayload.GetString(doc.RootElement, "sender") ?? string.Empty,
                    WebhookPayload.GetString(data, "profileName"),
                    ct);
            }

            return;
        }

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.InstanceName == evt.InstanceName, ct);
        if (number is null)
            return;

        // Repareamento divergente: a instância é conhecida, mas quem conectou é
        // outro número. Deixar seguir misturaria o histórico de dois WhatsApps
        // no mesmo cadastro.
        if (state == "open" && WebhookPayload.GetString(data, "wuid") is { Length: > 0 } wuid &&
            !PhoneNumber.SameNumber(number.Phone, PhoneNumber.FromJid(wuid)))
        {
            logger.LogWarning(
                "Instância {Instance} conectou com {Detected}, mas o cadastro é {Registered}: sessão derrubada.",
                evt.InstanceName, PhoneNumber.FromJid(wuid), number.Phone);

            number.Status = NumberStatus.WrongNumber;
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = number.Id,
                State = "wrong-number",
                ResultingStatus = NumberStatus.WrongNumber,
                OccurredAt = WebhookPayload.GetEnvelopeTime(doc, evt.ReceivedAt),
            });

            await pairing.LogoutDivergentAsync(number.InstanceName, ct);
            return;
        }

        int? statusReason = data.TryGetProperty("statusReason", out var reason) && reason.TryGetInt32(out var code)
            ? code
            : null;

        var newStatus = Resolve(state, statusReason, number.Status);
        number.Status = newStatus;

        var occurredAt = WebhookPayload.GetEnvelopeTime(doc, evt.ReceivedAt);

        // O 403 abre o cooldown de reconexão: reconectar durante a punição é o
        // que promove ban temporário a permanente (escalada 24h → 48h →
        // vitalício). 401/428/515 não são punição e não travam nada. Conectar
        // de novo encerra o cooldown — se o WhatsApp aceitou, o ban acabou.
        if (statusReason == 403)
            number.BannedUntil = occurredAt.AddHours(Math.Max(1, antiBan.Value.BanCooldownHours));
        else if (state == "open")
            number.BannedUntil = null;
        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = state,
            StatusReason = statusReason,
            ResultingStatus = newStatus,
            OccurredAt = occurredAt
        });

        // Downtime/uptime do dia mudou.
        await dirtyDays.MarkAsync(db, number.Id, occurredAt, ct);
    }

    private static NumberStatus Resolve(string state, int? statusReason, NumberStatus current)
    {
        if (state == "open")
            return NumberStatus.Active;

        if (statusReason == 403)
            return current == NumberStatus.BannedPermanent ? current : NumberStatus.BannedTemporary;

        // close sem reason (ex.: sintetizado pela reconciliação) não rebaixa um ban já registrado.
        if (statusReason is null && current is NumberStatus.BannedTemporary or NumberStatus.BannedPermanent)
            return current;

        return NumberStatus.Disconnected;
    }
}
