using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Contacts;

public sealed class ContactQueries(AppDbContext db)
{
    // Teto da exportação: acima disso o arquivo deixa de ser planilha e vira dump.
    public const int MaxRows = 50_000;

    private sealed record ConversationAgg(
        Guid ContactId, Guid ConversationId, Guid NumberId, Guid SellerId, DateTime FirstAt, DateTime LastAt);

    private sealed record NumberInfo(Guid Id, string Phone, NumberStatus Status);

    // Carga em lote (6 queries, independentemente da quantidade de contatos) e
    // agrupamento em memória — mesmo desenho do ReportQueries.
    public async Task<IReadOnlyList<ContactRowDto>> ListAsync(ContactFilter filter, CancellationToken ct)
    {
        // Todos os números: o filtro por vendedor é aplicado na CONVERSA, que
        // carimba quem atendeu. Filtrar pelo dono atual do número faria uma
        // transferência trocar de vendedor todo o histórico do contato.
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Select(n => new NumberInfo(n.Id, n.Phone, n.Status))
            .ToListAsync(ct);

        if (numbers.Count == 0)
            return [];

        var sellerNames = await db.Set<Seller>().AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var numberById = numbers.ToDictionary(n => n.Id);

        var activity = db.Set<Message>().AsNoTracking()
            .Join(db.Set<Conversation>().AsNoTracking(), m => m.ConversationId, c => c.Id,
                (m, c) => new { m.Timestamp, ConversationId = c.Id, c.ContactId, c.WhatsappNumberId, c.SellerId });

        if (filter.SellerId is { } sellerId)
            activity = activity.Where(x => x.SellerId == sellerId);
        if (filter.FromUtc is { } fromUtc)
            activity = activity.Where(x => x.Timestamp >= fromUtc);
        if (filter.ToUtc is { } toUtc)
            activity = activity.Where(x => x.Timestamp < toUtc);

        var conversations = await activity
            .GroupBy(x => new { x.ContactId, x.ConversationId, x.WhatsappNumberId, x.SellerId })
            .Select(g => new ConversationAgg(
                g.Key.ContactId,
                g.Key.ConversationId,
                g.Key.WhatsappNumberId,
                g.Key.SellerId,
                g.Min(x => x.Timestamp),
                g.Max(x => x.Timestamp)))
            .ToListAsync(ct);

        if (conversations.Count == 0)
            return [];

        var conversationIds = conversations.Select(c => c.ConversationId).ToArray();
        var contactIds = conversations.Select(c => c.ContactId).Distinct().ToArray();

        var contacts = await db.Set<Contact>().AsNoTracking()
            .Where(c => contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.RemoteJid, c.PushName })
            .ToListAsync(ct);

        var outcomes = await db.Set<ConversationOutcome>().AsNoTracking()
            .Where(o => conversationIds.Contains(o.ConversationId))
            .Select(o => new { o.ConversationId, o.OutcomeTypeCode, o.MarkedAt })
            .ToListAsync(ct);

        var labels = await db.Set<ConversationLabel>().AsNoTracking()
            .Where(l => conversationIds.Contains(l.ConversationId) && l.RemovedAt == null)
            .Select(l => new { l.ConversationId, l.LabelId, l.LabelName })
            .ToListAsync(ct);

        var typeNames = await db.Set<ConversationOutcomeType>().AsNoTracking()
            .ToDictionaryAsync(t => t.Code, t => t.Name, StringComparer.Ordinal, ct);

        // Fallback do nome quando a associação foi registrada antes de o LABELS_EDIT chegar.
        var labelNames = (await db.Set<WhatsappLabel>().AsNoTracking()
                .Select(l => new { l.LabelId, l.Name })
                .ToListAsync(ct))
            .GroupBy(l => l.LabelId)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var contactById = contacts.ToDictionary(c => c.Id);
        var outcomeByConversation = outcomes.ToLookup(o => o.ConversationId);
        var labelsByConversation = labels.ToLookup(l => l.ConversationId);

        var rows = conversations
            .GroupBy(c => c.ContactId)
            .Select(group =>
            {
                var contact = contactById[group.Key];
                var phone = PhoneOf(contact.RemoteJid);

                // O atendimento mais recente manda: dele saem o número, o status e
                // o vendedor — este último carimbado na conversa, não o dono atual.
                var latest = group.MaxBy(c => c.LastAt)!;
                var responsible = numberById[latest.NumberId];

                var outcome = group
                    .SelectMany(c => outcomeByConversation[c.ConversationId])
                    .OrderByDescending(o => o.MarkedAt)
                    .FirstOrDefault();

                var active = group
                    .SelectMany(c => labelsByConversation[c.ConversationId])
                    .Select(l => l.LabelName ?? labelNames.GetValueOrDefault(l.LabelId) ?? l.LabelId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ContactRowDto(
                    contact.Id,
                    string.IsNullOrWhiteSpace(contact.PushName) ? phone : contact.PushName,
                    phone,
                    group.Min(c => c.FirstAt),
                    group.Max(c => c.LastAt),
                    outcome?.OutcomeTypeCode,
                    outcome is null ? null : typeNames.GetValueOrDefault(outcome.OutcomeTypeCode, outcome.OutcomeTypeCode),
                    active,
                    latest.SellerId,
                    sellerNames.GetValueOrDefault(latest.SellerId, "—"),
                    responsible.Phone,
                    responsible.Status.ToString(),
                    responsible.Status is NumberStatus.BannedTemporary or NumberStatus.BannedPermanent);
            });

        // Desfecho e banimento só existem depois de escolher o vendedor responsável
        // e a etiqueta vencedora do contato — por isso filtram aqui, não na query.
        if (filter.OutcomeTypes.Count > 0)
        {
            var wanted = filter.OutcomeTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var includeNone = wanted.Contains(ContactFilter.NoOutcome);
            rows = rows.Where(r => r.OutcomeTypeCode is null ? includeNone : wanted.Contains(r.OutcomeTypeCode));
        }

        if (filter.Banned is { } banned)
            rows = rows.Where(r => r.NumberBanned == banned);

        return [.. rows.OrderByDescending(r => r.LastMessageAt).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // "5511999998888:12@s.whatsapp.net" → "5511999998888".
    private static string PhoneOf(string remoteJid)
    {
        var raw = remoteJid.AsSpan();
        var at = raw.IndexOf('@');
        if (at >= 0)
            raw = raw[..at];

        var device = raw.IndexOf(':');
        return device >= 0 ? raw[..device].ToString() : raw.ToString();
    }
}
