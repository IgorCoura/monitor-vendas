using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Conversations;

public sealed class MessageUpsertHandler(
    IOptions<MetricsOptions> options,
    IDirtyDayTracker dirtyDays,
    ILogger<MessageUpsertHandler> logger) : IWebhookEventHandler
{
    public string EventType => "MESSAGES_UPSERT";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data ||
            !data.TryGetProperty("key", out var key))
        {
            logger.LogWarning("MESSAGES_UPSERT {Id} sem data.key; ignorado.", evt.Id);
            return;
        }

        var remoteJid = WebhookPayload.GetString(key, "remoteJid");
        var waMessageId = WebhookPayload.GetString(key, "id");
        if (remoteJid is null || waMessageId is null)
            return;

        // Grupos e broadcasts ficam fora das métricas (decisão da V1).
        if (WebhookPayload.IsGroupOrBroadcast(remoteJid))
            return;

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.InstanceName == evt.InstanceName, ct);
        if (number is null)
        {
            // Instância sem cadastro é, em regra, uma tentativa de pareamento em
            // curso: o WhatsApp despeja o histórico assim que conecta, e isso não
            // pode virar dado antes de sabermos de quem é o número.
            logger.LogDebug("Instância {Instance} não cadastrada; mensagem ignorada.", evt.InstanceName);
            return;
        }

        // Quarentena: o número conectou com outro WhatsApp e está em revisão.
        if (number.Status == NumberStatus.WrongNumber)
        {
            logger.LogWarning("Número {Phone} está em quarentena; mensagem descartada.", number.Phone);
            return;
        }

        var alreadyStored = await db.Set<Message>()
            .AnyAsync(m => m.WhatsappNumberId == number.Id && m.WaMessageId == waMessageId, ct);
        if (alreadyStored)
            return;

        var fromMe = WebhookPayload.GetBool(key, "fromMe");

        // Mensagem que o próprio sistema mandou (envio da lista de contatos) não é
        // atividade do vendedor — contaria como mensagem enviada e sujaria a métrica.
        if (fromMe && await db.Set<ContactShareMessage>().AnyAsync(m => m.WaMessageId == waMessageId, ct))
            return;

        var timestamp = WebhookPayload.GetUnixTimestamp(data, "messageTimestamp") ?? evt.ReceivedAt;
        var pushName = WebhookPayload.GetString(data, "pushName");

        var contact = await db.Set<Contact>().FirstOrDefaultAsync(c => c.RemoteJid == remoteJid, ct);
        if (contact is null)
        {
            contact = new Contact
            {
                Id = Guid.NewGuid(),
                RemoteJid = remoteJid,
                PushName = fromMe ? null : pushName,
                CreatedAt = timestamp
            };
            db.Add(contact);
        }
        else if (!fromMe && !string.IsNullOrWhiteSpace(pushName))
        {
            contact.PushName = pushName;
        }

        var conversation = await db.Set<Conversation>()
            .Where(c => c.WhatsappNumberId == number.Id && c.ContactId == contact.Id)
            .OrderByDescending(c => c.LastMessageAt)
            .FirstOrDefaultAsync(ct);

        var windowDays = options.Value.NewConversationWindowDays;
        if (conversation is null || timestamp - conversation.LastMessageAt > TimeSpan.FromDays(windowDays))
        {
            conversation = new Conversation
            {
                Id = Guid.NewGuid(),
                WhatsappNumberId = number.Id,
                SellerId = number.SellerId,
                ContactId = contact.Id,
                StartedByContact = !fromMe,
                StartedAt = timestamp,
                LastMessageAt = timestamp
            };
            db.Add(conversation);
        }
        else if (timestamp > conversation.LastMessageAt)
        {
            conversation.LastMessageAt = timestamp;
        }

        var text = WebhookPayload.ExtractText(data);

        db.Add(new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            WhatsappNumberId = number.Id,
            SellerId = number.SellerId,
            WaMessageId = waMessageId,
            Direction = fromMe ? MessageDirection.Outbound : MessageDirection.Inbound,
            Type = WebhookPayload.GetString(data, "messageType") ?? "unknown",
            Text = text,
            DurationSeconds = WebhookPayload.ExtractDurationSeconds(data),
            Timestamp = timestamp
        });

        // "SAIR"/"PARE" do cliente vira opt-out permanente. Só mensagem DELE
        // conta: o vendedor escrevendo "pare" não descadastra ninguém.
        if (!fromMe && OptOutDetector.IsOptOut(text)
            && !await db.Set<ContactOptOut>().AnyAsync(o => o.ContactId == contact.Id, ct))
        {
            db.Add(new ContactOptOut
            {
                Id = Guid.NewGuid(),
                ContactId = contact.Id,
                Reason = OptOutReason.Requested,
                Evidence = text!.Length > 200 ? text[..200] : text,
                CreatedAt = timestamp,
            });
        }

        await dirtyDays.MarkAsync(db, number.Id, timestamp, ct);
    }
}
