using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Numbers;

// Instância removida ou deslogada do lado da Evolution. Nenhum connection.update
// acompanha esses casos, então sem estes handlers o número seguia "conectado" no
// painel enquanto a instância nem existia mais — o número fantasma.
public sealed class InstanceRemovedHandler(IDirtyDayTracker dirtyDays) : IWebhookEventHandler
{
    public string EventType => "REMOVE_INSTANCE";

    public Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct) =>
        InstanceGone.MarkDisconnectedAsync(evt, "removed", db, dirtyDays, ct);
}

public sealed class InstanceLogoutHandler(IDirtyDayTracker dirtyDays) : IWebhookEventHandler
{
    public string EventType => "LOGOUT_INSTANCE";

    public Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct) =>
        InstanceGone.MarkDisconnectedAsync(evt, "logout", db, dirtyDays, ct);
}

internal static class InstanceGone
{
    // Só o Active é rebaixado: ban (temporário ou permanente) é estado mais
    // forte que "sem instância" e não pode ser apagado por um logout — o mesmo
    // critério do connection.update close.
    public static async Task MarkDisconnectedAsync(
        WebhookEvent evt, string state, AppDbContext db, IDirtyDayTracker dirtyDays, CancellationToken ct)
    {
        var number = await db.Set<WhatsappNumber>()
            .FirstOrDefaultAsync(n => n.InstanceName == evt.InstanceName, ct);
        if (number is null || number.Status != NumberStatus.Active)
            return;

        using var doc = JsonDocument.Parse(evt.Payload);
        var occurredAt = WebhookPayload.GetEnvelopeTime(doc, evt.ReceivedAt);

        number.Status = NumberStatus.Disconnected;
        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = state,
            ResultingStatus = NumberStatus.Disconnected,
            OccurredAt = occurredAt,
        });

        // Downtime do dia mudou.
        await dirtyDays.MarkAsync(db, number.Id, occurredAt, ct);
    }
}
