using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Conversations;

public sealed class LabelsEditHandler(OutcomeCatalogVersion catalogVersion) : IWebhookEventHandler
{
    public string EventType => "LABELS_EDIT";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data)
            return;

        var labelId = WebhookPayload.GetString(data, "labelId") ?? WebhookPayload.GetString(data, "id");
        var name = WebhookPayload.GetString(data, "name");
        if (labelId is null)
            return;

        var deleted = WebhookPayload.GetBool(data, "deleted");
        var existing = await db.Set<WhatsappLabel>()
            .FirstOrDefaultAsync(l => l.InstanceName == evt.InstanceName && l.LabelId == labelId, ct);

        if (deleted)
        {
            if (existing is not null)
                db.Remove(existing);
            return;
        }

        if (name is null)
            return;

        if (existing is null)
        {
            db.Add(new WhatsappLabel { Id = Guid.NewGuid(), InstanceName = evt.InstanceName, LabelId = labelId, Name = name });
        }
        else if (existing.Name != name)
        {
            // Renomear a etiqueta pode mudar o tipo que ela representa.
            existing.Name = name;
            catalogVersion.Bump();
        }
    }
}

public sealed class LabelsAssociationHandler(
    OutcomeResolver resolver,
    IDirtyDayTracker dirtyDays) : IWebhookEventHandler
{
    public string EventType => "LABELS_ASSOCIATION";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data)
            return;

        // Alguns builds aninham os campos em "association".
        if (data.TryGetProperty("association", out var nested) && nested.ValueKind == JsonValueKind.Object)
            data = nested;

        var labelId = WebhookPayload.GetString(data, "labelId");
        var chatId = WebhookPayload.GetString(data, "chatId") ?? WebhookPayload.GetString(data, "remoteJid");
        var type = WebhookPayload.GetString(data, "type")?.ToLowerInvariant();
        if (labelId is null || chatId is null || type is not ("add" or "remove"))
            return;

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.InstanceName == evt.InstanceName, ct);
        var contact = await db.Set<Contact>().FirstOrDefaultAsync(c => c.RemoteJid == chatId, ct);
        // Número em quarentena (conectou com outro WhatsApp) não recebe nada.
        if (number is null || contact is null || number.Status == NumberStatus.WrongNumber)
            return;

        var conversation = await db.Set<Conversation>()
            .Where(c => c.WhatsappNumberId == number.Id && c.ContactId == contact.Id)
            .OrderByDescending(c => c.LastMessageAt)
            .FirstOrDefaultAsync(ct);
        if (conversation is null)
            return;

        var label = await db.Set<WhatsappLabel>().AsNoTracking()
            .FirstOrDefaultAsync(l => l.InstanceName == evt.InstanceName && l.LabelId == labelId, ct);
        var occurredAt = WebhookPayload.GetEnvelopeTime(doc, evt.ReceivedAt);

        // Toda associação é registrada, mapeada ou não — é o histórico que permite
        // reavaliar o passado quando o catálogo de etiquetas muda.
        var association = await db.Set<ConversationLabel>()
            .FirstOrDefaultAsync(l => l.ConversationId == conversation.Id && l.LabelId == labelId, ct);

        if (type == "add")
        {
            if (association is null)
            {
                association = new ConversationLabel
                {
                    ConversationId = conversation.Id,
                    LabelId = labelId,
                    LabelName = label?.Name,
                    AppliedAt = occurredAt,
                };
                db.Add(association);
            }
            else
            {
                association.AppliedAt = occurredAt;
                association.RemovedAt = null;
                association.LabelName = label?.Name ?? association.LabelName;
            }
        }
        else if (association is not null)
        {
            association.RemovedAt = occurredAt;
        }

        await db.SaveChangesAsync(ct);
        await resolver.ResolveAsync(db, [conversation.Id], ct);
        await dirtyDays.MarkAsync(db, number.Id, occurredAt, ct);
    }
}
