using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.Outcomes;

// Depois de qualquer mudança no catálogo (tipo ou etiqueta aceita), reavalia os
// desfechos a partir do histórico de etiquetas registrado e marca os dias
// afetados para o agregado se refazer.
public sealed class OutcomeReconciler(
    AppDbContext db,
    OutcomeResolver resolver,
    IDirtyDayTracker dirtyDays,
    ReportCacheVersion cacheVersion,
    DailyMetricsBuilder builder,
    ILogger<OutcomeReconciler> logger)
{
    public async Task<int> ReconcileAllAsync(CancellationToken ct)
    {
        // Só conversas que já tiveram alguma etiqueta podem mudar de desfecho.
        var conversationIds = await db.Set<ConversationLabel>().AsNoTracking()
            .Select(l => l.ConversationId)
            .Distinct()
            .ToListAsync(ct);

        if (conversationIds.Count == 0)
        {
            cacheVersion.Bump();
            return 0;
        }

        var before = await db.Set<ConversationOutcome>().AsNoTracking()
            .Where(o => conversationIds.Contains(o.ConversationId))
            .Select(o => new { o.ConversationId, o.OutcomeTypeCode, o.MarkedAt })
            .ToListAsync(ct);

        var changed = await resolver.ResolveAsync(db, conversationIds, ct);
        await db.SaveChangesAsync(ct);

        var after = await db.Set<ConversationOutcome>().AsNoTracking()
            .Where(o => conversationIds.Contains(o.ConversationId))
            .Select(o => new { o.ConversationId, o.OutcomeTypeCode, o.MarkedAt })
            .ToListAsync(ct);

        // Dias afetados = onde havia desfecho antes + onde há agora.
        var affected = before.Select(o => (o.ConversationId, o.MarkedAt))
            .Concat(after.Select(o => (o.ConversationId, o.MarkedAt)))
            .Distinct()
            .ToList();

        if (affected.Count > 0)
        {
            var ids = affected.Select(a => a.ConversationId).Distinct().ToList();
            var numberByConversation = await db.Set<Conversation>().AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.WhatsappNumberId })
                .ToListAsync(ct);

            var lookup = numberByConversation.ToDictionary(x => x.Id, x => x.WhatsappNumberId);
            foreach (var (conversationId, markedAt) in affected)
            {
                if (lookup.TryGetValue(conversationId, out var numberId))
                    await dirtyDays.MarkAsync(db, numberId, markedAt, ct);
            }
        }

        cacheVersion.Bump();
        await builder.ProcessDirtyDaysAsync(ct: ct);

        logger.LogInformation("Reconciliação de desfechos: {Changed} conversas alteradas.", changed);
        return changed;
    }
}
