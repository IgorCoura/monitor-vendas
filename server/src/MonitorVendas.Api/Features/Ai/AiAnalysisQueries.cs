using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiAnalysisFilter(
    DateTime From,
    DateTime To,
    Guid? SellerId,
    string? Status,
    string? LossReason,
    bool? Divergent,
    bool? Recontact);

// A leitura das análises já feitas. A tela pagina, a exportação leva tudo — o
// mesmo filtro e a mesma regra de divergência nos dois, porque duas consultas
// para a mesma pergunta seria garantir que um dia elas divergissem.
public sealed class AiAnalysisQueries(AppDbContext db)
{
    // Teto da planilha, como na exportação de contatos: acima disso o arquivo
    // sai cortado e a resposta denuncia o corte.
    public const int MaxRows = 50_000;

    public async Task<(List<AiAnalysisRowDto> Items, int Total)> ListAsync(
        AiAnalysisFilter filter,
        int? page = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var typeNames = await db.Set<ConversationOutcomeType>().AsNoTracking()
            .ToDictionaryAsync(t => t.Code, t => t.Name, ct);

        var query =
            from analysis in db.Set<ConversationAiAnalysis>().AsNoTracking().Where(a => a.IsCurrent)
            join conversation in db.Set<Conversation>().AsNoTracking() on analysis.ConversationId equals conversation.Id
            join number in db.Set<WhatsappNumber>().AsNoTracking() on conversation.WhatsappNumberId equals number.Id
            // O vendedor é o carimbado na conversa: número transferido não muda
            // quem atendeu no passado.
            join seller in db.Set<Seller>().AsNoTracking() on conversation.SellerId equals seller.Id
            join contact in db.Set<Contact>().AsNoTracking() on conversation.ContactId equals contact.Id
            join outcome in db.Set<ConversationOutcome>().AsNoTracking() on conversation.Id equals outcome.ConversationId into outcomes
            from outcome in outcomes.DefaultIfEmpty()
            where conversation.LastMessageAt >= filter.From && conversation.StartedAt <= filter.To
            select new
            {
                Analysis = analysis,
                Conversation = conversation,
                Seller = seller,
                Number = number,
                contact.PushName,
                contact.RemoteJid,
                RealCode = outcome != null ? outcome.OutcomeTypeCode : null,
            };

        if (filter.SellerId is not null)
            query = query.Where(x => x.Seller.Id == filter.SellerId);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(x => x.Analysis.StatusCode == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.LossReason))
            query = query.Where(x => x.Analysis.LossReason == filter.LossReason);

        if (filter.Recontact is not null)
            query = query.Where(x => x.Analysis.ShouldRecontact == filter.Recontact);

        // Divergência é comparação com a etiqueta; "em andamento" conta como
        // ausência de desfecho, igual ao AiRowMapper.
        if (filter.Divergent is not null)
        {
            query = filter.Divergent.Value
                ? query.Where(x => x.Analysis.StatusCode == ConversationAiAnalysis.Open
                    ? x.RealCode != null
                    : x.Analysis.StatusCode != x.RealCode)
                : query.Where(x => x.Analysis.StatusCode == ConversationAiAnalysis.Open
                    ? x.RealCode == null
                    : x.Analysis.StatusCode == x.RealCode);
        }

        var total = await query.CountAsync(ct);

        query = query.OrderByDescending(x => x.Conversation.LastMessageAt);
        if (page is not null && pageSize is not null)
            query = query.Skip((Math.Max(1, page.Value) - 1) * pageSize.Value).Take(pageSize.Value);
        else
            query = query.Take(MaxRows);

        var rows = await query.ToListAsync(ct);

        var conversationIds = rows.Select(r => r.Conversation.Id).ToList();
        var versions = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => conversationIds.Contains(a.ConversationId))
            .GroupBy(a => a.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, ct);

        var items = rows.Select(r =>
        {
            var aiCode = r.Analysis.StatusCode == ConversationAiAnalysis.Open ? null : r.Analysis.StatusCode;
            var phone = ConversationAiWorkset.PhoneOf(r.RemoteJid) ?? "—";

            return new AiAnalysisRowDto(
                r.Conversation.Id,
                r.Analysis.Id,
                r.Seller.Id,
                r.Seller.Name,
                r.Number.Phone,
                r.PushName ?? phone,
                phone,
                r.Conversation.StartedAt,
                r.Conversation.LastMessageAt,
                r.RealCode is null ? null : typeNames.GetValueOrDefault(r.RealCode, r.RealCode),
                aiCode is null ? "Em andamento" : typeNames.GetValueOrDefault(aiCode, aiCode),
                r.Analysis.StatusCode,
                r.Analysis.StatusConfidence,
                !string.Equals(aiCode, r.RealCode, StringComparison.OrdinalIgnoreCase),
                r.Analysis.StatusEvidence,
                AiAnalysisSchema.FriendlyLossReason(r.Analysis.LossReason),
                r.Analysis.AskedForSale,
                r.Analysis.IgnoredBuyingSignal,
                r.Analysis.Objections,
                r.Analysis.ShouldRecontact,
                r.Analysis.RecontactReason,
                r.Analysis.SuggestedMessage,
                r.Analysis.Interest,
                r.Analysis.Summary,
                r.Analysis.ConductAlert,
                r.Analysis.Model,
                r.Analysis.AnalyzedAt,
                versions.GetValueOrDefault(r.Conversation.Id, 1),
                r.Analysis.AudioExpected,
                r.Analysis.AudioAttached);
        }).ToList();

        return (items, total);
    }

    // A síntese mais recente de cada vendedor, marcada como desatualizada quando
    // as leituras que a geraram já não são as correntes.
    public async Task<List<AiSynthesisDto>> SynthesesAsync(Guid? sellerId, CancellationToken ct = default)
    {
        var query = db.Set<SellerAiSynthesis>().AsNoTracking().AsQueryable();
        if (sellerId is not null)
            query = query.Where(s => s.SellerId == sellerId);

        var latest = await query
            .GroupBy(s => s.SellerId)
            .Select(g => g.OrderByDescending(s => s.CreatedAt).First())
            .ToListAsync(ct);

        var currentBySeller = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => a.IsCurrent)
            .Join(db.Set<Conversation>().AsNoTracking(), a => a.ConversationId, c => c.Id, (a, c) => new { a.Id, c.WhatsappNumberId })
            .Join(db.Set<WhatsappNumber>().AsNoTracking(), x => x.WhatsappNumberId, n => n.Id, (x, n) => new { x.Id, n.SellerId })
            .ToListAsync(ct);

        var hashes = currentBySeller
            .GroupBy(x => x.SellerId)
            .ToDictionary(g => g.Key, g => SellerAiSynthesis.HashOf(g.Select(x => x.Id)));

        return [.. latest.Select(s => new AiSynthesisDto(
            s.SellerId,
            s.SellerName,
            s.Overview,
            SellerAiSynthesis.Split(s.Strengths),
            SellerAiSynthesis.Split(s.Improvements),
            s.DominantLossPattern,
            s.TrainingSuggestion,
            s.ConversationsCount,
            s.Model,
            s.CreatedAt,
            hashes.GetValueOrDefault(s.SellerId) != s.InputsHash))];
    }
}
