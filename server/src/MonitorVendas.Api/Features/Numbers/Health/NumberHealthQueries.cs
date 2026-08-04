using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Numbers.Health;

public sealed record HealthSignalDto(string Key, string Value, int Points);

public sealed record NumberHealthDto(
    Guid NumberId,
    string Phone,
    Guid SellerId,
    string SellerName,
    string Status,
    int Score,
    string Level,
    IReadOnlyList<HealthSignalDto> Signals);

// Carrega as contagens em LOTE (uma consulta por sinal, nunca por número) e
// delega o julgamento ao NumberHealth — a régua mora num lugar só.
public sealed class NumberHealthQueries(AppDbContext db)
{
    public async Task<IReadOnlyList<NumberHealthDto>> ListAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id, (n, s) => new { Number = n, SellerName = s.Name })
            .OrderBy(x => x.SellerName).ThenBy(x => x.Number.Phone)
            .ToListAsync(ct);

        // A janela de 15 min é o que faz a taxa de entrega funcionar: sem ela,
        // toda mensagem recém-enviada contaria como não entregue e o número
        // pareceria doente o tempo todo.
        var deliveryCutoff = DateTime.UtcNow.AddMinutes(-15);
        var delivery = await db.Set<Message>().AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound
                && m.Timestamp >= fromUtc && m.Timestamp < toUtc && m.Timestamp <= deliveryCutoff)
            .GroupBy(m => m.WhatsappNumberId)
            .Select(g => new { g.Key, Considered = g.Count(), Missing = g.Count(m => m.DeliveredAt == null) })
            .ToDictionaryAsync(x => x.Key, ct);

        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => c.StartedAt >= fromUtc && c.StartedAt < toUtc)
            .GroupBy(c => c.WhatsappNumberId)
            .Select(g => new
            {
                g.Key,
                Inbound = g.Count(c => c.StartedByContact),
                Outbound = g.Count(c => !c.StartedByContact),
            })
            .ToDictionaryAsync(x => x.Key, ct);

        var replied = await db.Set<Conversation>().AsNoTracking()
            .Where(c => c.StartedAt >= fromUtc && c.StartedAt < toUtc && c.StartedByContact)
            .Where(c => db.Set<Message>().Any(m => m.ConversationId == c.Id && m.Direction == MessageDirection.Outbound))
            .GroupBy(c => c.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Desconexões olham só as últimas 24h da janela: instabilidade antiga
        // não é sinal de agora.
        var disconnectFloor = toUtc.AddHours(-24);
        var disconnections = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.OccurredAt >= disconnectFloor && e.OccurredAt < toUtc
                && e.ResultingStatus == NumberStatus.Disconnected)
            .GroupBy(e => e.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var bans = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.OccurredAt >= fromUtc && e.OccurredAt < toUtc && e.StatusReason == 403)
            .GroupBy(e => e.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var days = Math.Max(1.0, (toUtc - fromUtc).TotalDays);
        var result = new List<NumberHealthDto>(numbers.Count);

        foreach (var entry in numbers)
        {
            var n = entry.Number;
            var d = delivery.GetValueOrDefault(n.Id);
            var c = conversations.GetValueOrDefault(n.Id);

            var health = NumberHealth.Evaluate(new NumberHealthInput(
                DeliveryConsidered: d?.Considered ?? 0,
                DeliveryMissing: d?.Missing ?? 0,
                InboundConversations: c?.Inbound ?? 0,
                InboundConversationsReplied: replied.GetValueOrDefault(n.Id),
                OutboundConversations: c?.Outbound ?? 0,
                DisconnectionsLast24h: disconnections.GetValueOrDefault(n.Id),
                NewContactsPerDay: (c?.Outbound ?? 0) / days,
                SendRestricted: n.SendingPausedUntil is { } paused && paused > fromUtc,
                BanEvents: bans.GetValueOrDefault(n.Id)));

            result.Add(new NumberHealthDto(
                n.Id, n.Phone, n.SellerId, entry.SellerName, n.Status.ToString(),
                health.Score, health.Level.ToString(),
                [.. health.Signals.Select(s => new HealthSignalDto(s.Key, s.Value, s.Points))]));
        }

        return result;
    }
}
