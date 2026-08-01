using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Conversations;

// Atualiza o ciclo de vida da mensagem enviada: DELIVERY_ACK → entregue, READ → lida.
public sealed class MessageUpdateHandler(IDirtyDayTracker dirtyDays) : IWebhookEventHandler
{
    public string EventType => "MESSAGES_UPDATE";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data)
            return;

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.InstanceName == evt.InstanceName, ct);
        // Número em quarentena (conectou com outro WhatsApp) não recebe nada.
        if (number is null || number.Status == NumberStatus.WrongNumber)
            return;

        // O payload pode vir como objeto único ou como array de updates.
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in data.EnumerateArray())
                await ApplyAsync(update, number.Id, evt.ReceivedAt, db, ct);
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            await ApplyAsync(data, number.Id, evt.ReceivedAt, db, ct);
        }
    }

    private async Task ApplyAsync(JsonElement update, Guid numberId, DateTime receivedAt, AppDbContext db, CancellationToken ct)
    {
        var waMessageId = WebhookPayload.GetString(update, "keyId") ?? WebhookPayload.GetString(update, "messageId");
        if (waMessageId is null && update.TryGetProperty("key", out var key))
            waMessageId = WebhookPayload.GetString(key, "id");

        var status = WebhookPayload.GetString(update, "status");
        if (waMessageId is null || status is null)
            return;

        var message = await db.Set<Message>()
            .FirstOrDefaultAsync(m => m.WhatsappNumberId == numberId && m.WaMessageId == waMessageId, ct);
        if (message is null)
            return;

        switch (status.ToUpperInvariant())
        {
            case "DELIVERY_ACK":
                message.DeliveredAt ??= receivedAt;
                break;
            case "READ":
                message.DeliveredAt ??= receivedAt;
                message.ReadAt ??= receivedAt;
                // A taxa de leitura é atribuída ao dia da MENSAGEM, não do ack.
                await dirtyDays.MarkAsync(db, numberId, message.Timestamp, ct);
                break;
        }
    }
}
