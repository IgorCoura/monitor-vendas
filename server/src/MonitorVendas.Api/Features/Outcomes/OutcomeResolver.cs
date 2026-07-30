using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Api.Features.Outcomes;

// O desfecho de uma conversa é SEMPRE derivado das etiquetas ativas: vale a
// etiqueta mapeada aplicada mais recentemente ("a última que vale"). Remover a
// etiqueta atual faz a anterior ainda ativa voltar a valer; sem etiqueta mapeada
// ativa, a conversa fica sem desfecho.
//
// Como é uma derivação pura do histórico, o handler do webhook e o reconciliador
// usam exatamente o mesmo caminho — não há duas regras para divergir.
public sealed class OutcomeResolver(OutcomeLabelMatcher matcher)
{
    public async Task<int> ResolveAsync(AppDbContext db, IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return 0;

        var ids = conversationIds.ToArray();
        var map = await matcher.GetMapAsync(db, ct);

        var labels = await db.Set<ConversationLabel>().AsNoTracking()
            .Where(l => ids.Contains(l.ConversationId) && l.RemovedAt == null)
            .ToListAsync(ct);

        var outcomes = await db.Set<ConversationOutcome>()
            .Where(o => ids.Contains(o.ConversationId))
            .ToListAsync(ct);

        var labelsByConversation = labels.ToLookup(l => l.ConversationId);
        var outcomeByConversation = outcomes.ToDictionary(o => o.ConversationId);
        var changed = 0;

        foreach (var conversationId in ids)
        {
            var winner = labelsByConversation[conversationId]
                .Select(l => new { Label = l, TypeCode = map.GetValueOrDefault(LabelNormalizer.Normalize(l.LabelName)) })
                .Where(x => x.TypeCode is not null)
                .OrderByDescending(x => x.Label.AppliedAt)
                .ThenByDescending(x => x.Label.LabelId, StringComparer.Ordinal)
                .FirstOrDefault();

            var existing = outcomeByConversation.GetValueOrDefault(conversationId);

            if (winner is null)
            {
                if (existing is not null)
                {
                    db.Remove(existing);
                    changed++;
                }
                continue;
            }

            if (existing is null)
            {
                db.Add(new ConversationOutcome
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    OutcomeTypeCode = winner.TypeCode!,
                    LabelId = winner.Label.LabelId,
                    MarkedAt = winner.Label.AppliedAt,
                });
                changed++;
            }
            else if (existing.OutcomeTypeCode != winner.TypeCode
                || existing.LabelId != winner.Label.LabelId
                || existing.MarkedAt != winner.Label.AppliedAt)
            {
                existing.OutcomeTypeCode = winner.TypeCode!;
                existing.LabelId = winner.Label.LabelId;
                existing.MarkedAt = winner.Label.AppliedAt;
                changed++;
            }
        }

        return changed;
    }
}
