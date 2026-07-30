using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Ai.Analysis;

// Conversa pronta para ir à IA: transcrição montada e o contexto que a planilha
// e a tela precisam ao lado. Vive aqui porque a exportação e a tela de análises
// carregam exatamente o mesmo conjunto — duplicar isso era garantir divergência.
public sealed record ConversationContext(
    Guid ConversationId,
    Guid? SellerId,
    string SellerName,
    string SellerNumber,
    string ContactName,
    string ContactPhone,
    DateTime StartedAt,
    DateTime LastMessageAt,
    string? RealOutcomeCode,
    ConversationAnalysisInput Input);

public sealed record ConversationAiFilter(
    DateTime From,
    DateTime To,
    IReadOnlyList<Guid> SellerIds,
    int MaxConversations,
    bool Force = false);

public sealed class ConversationAiWorkset(AppDbContext db, ReportQueries queries, IOptions<MetricsOptions> options)
{
    private sealed record NumberRow(Guid Id, string Phone, Guid SellerId, string SellerName);

    public async Task<(List<ConversationContext> Items, bool Truncated)> LoadAsync(
        ConversationAiFilter filter,
        CancellationToken ct = default)
    {
        var numbers = (await NumbersAsync(filter, ct)).ToDictionary(n => n.Id);
        var max = Math.Max(1, filter.MaxConversations);

        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => numbers.Keys.Contains(c.WhatsappNumberId) &&
                        c.LastMessageAt >= filter.From && c.StartedAt <= filter.To)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(max + 1)
            .ToListAsync(ct);

        var truncated = conversations.Count > max;
        if (truncated)
            conversations.RemoveAt(conversations.Count - 1);

        if (conversations.Count == 0)
            return ([], false);

        var ids = conversations.Select(c => c.Id).ToList();
        var contactIds = conversations.Select(c => c.ContactId).ToList();

        var contacts = await db.Set<Contact>().AsNoTracking()
            .Where(c => contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var outcomes = await db.Set<ConversationOutcome>().AsNoTracking()
            .Where(o => ids.Contains(o.ConversationId))
            .ToDictionaryAsync(o => o.ConversationId, o => o.OutcomeTypeCode, ct);

        var messages = await db.Set<Message>().AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId))
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.ConversationId, m.Direction, m.Timestamp, m.Text, m.Type })
            .ToListAsync(ct);

        var byConversation = messages
            .GroupBy(m => m.ConversationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var calendar = await queries.BuildCalendarAsync(ct);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var staleAfter = options.Value.FollowUpGapBusinessHours;
        var now = DateTime.UtcNow;

        var items = conversations.Select(conversation =>
        {
            var rows = byConversation.TryGetValue(conversation.Id, out var list) ? list : [];
            var contact = contacts.GetValueOrDefault(conversation.ContactId);
            var number = numbers.GetValueOrDefault(conversation.WhatsappNumberId);
            var phone = PhoneOf(contact?.RemoteJid);
            var silence = calendar.BusinessTimeBetween(conversation.LastMessageAt, now).TotalHours;

            var transcript = TranscriptBuilder.Build(
                [.. rows.Select(r => new TranscriptMessage(r.Direction, r.Timestamp, r.Text, r.Type))],
                contact?.PushName,
                phone,
                timeZone,
                conversation.StartedByContact,
                silence);

            return new ConversationContext(
                conversation.Id,
                number?.SellerId,
                number?.SellerName ?? "—",
                number?.Phone ?? "—",
                contact?.PushName ?? phone ?? "—",
                phone ?? "—",
                conversation.StartedAt,
                conversation.LastMessageAt,
                outcomes.GetValueOrDefault(conversation.Id),
                new ConversationAnalysisInput(
                    conversation.Id,
                    rows.Count,
                    conversation.LastMessageAt,
                    transcript,
                    // Conversa parada além do silêncio configurado não pode ser
                    // classificada como "em andamento": o relógio decide antes da IA.
                    silence <= staleAfter,
                    filter.Force));
        }).ToList();

        return (items, truncated);
    }

    // Só os tipos ativos entram no schema: é o catálogo que o usuário mantém.
    public Task<List<OutcomeChoice>> CatalogAsync(CancellationToken ct = default) =>
        db.Set<ConversationOutcomeType>().AsNoTracking()
            .Where(t => t.Active)
            .OrderBy(t => t.SortOrder)
            .Select(t => new OutcomeChoice(t.Code, t.Name))
            .ToListAsync(ct);

    public Task<Dictionary<string, string>> TypeNamesAsync(CancellationToken ct = default) =>
        db.Set<ConversationOutcomeType>().AsNoTracking().ToDictionaryAsync(t => t.Code, t => t.Name, ct);

    public async Task<List<(Guid Id, string Phone, Guid SellerId, string SellerName)>> SellerNumbersAsync(
        ConversationAiFilter filter,
        CancellationToken ct = default) =>
        [.. (await NumbersAsync(filter, ct)).Select(n => (n.Id, n.Phone, n.SellerId, n.SellerName))];

    private async Task<List<NumberRow>> NumbersAsync(ConversationAiFilter filter, CancellationToken ct)
    {
        var numbers = db.Set<WhatsappNumber>().AsNoTracking();

        // O filtro vem antes da projeção: sobre o registro já montado o EF não
        // traduz o Contains e a consulta estoura em 500.
        if (filter.SellerIds.Count > 0)
        {
            var sellerIds = filter.SellerIds.ToList();
            numbers = numbers.Where(n => sellerIds.Contains(n.SellerId));
        }

        return await numbers
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id,
                (n, s) => new NumberRow(n.Id, n.Phone, s.Id, s.Name))
            .ToListAsync(ct);
    }

    public static string? PhoneOf(string? remoteJid) =>
        remoteJid is null ? null : new string([.. remoteJid.TakeWhile(c => c is not '@' and not ':').Where(char.IsDigit)]);
}
