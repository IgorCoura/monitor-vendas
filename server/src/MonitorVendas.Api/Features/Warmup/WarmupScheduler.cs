using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Features.Warmup;

public interface IWarmupScheduler
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

// Decide QUEM fala com QUEM e QUANDO. Não envia nada — quem envia é o executor.
public sealed class WarmupScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<WarmupOptions> options,
    IRandomSource random,
    ILogger<WarmupScheduler> logger) : IWarmupScheduler
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<IWarmupState>();
        var generator = scope.ServiceProvider.GetRequiredService<IWarmupContentGenerator>();
        var reports = scope.ServiceProvider.GetRequiredService<ReportQueries>();
        var clock = scope.ServiceProvider.GetRequiredService<WarmupClock>();

        if (!await state.IsRunningAsync(db, ct))
            return 0;

        var settings = options.Value;
        var now = DateTime.UtcNow;

        // Geração em recuo: nem tenta. Insistir contra uma quota diária esgotada
        // só queima o que a análise de conversas ainda poderia usar.
        var stored = await db.Set<WarmupSettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);
        if (stored?.GenerationPausedUntil is { } until && until > now)
            return 0;

        // O grafo cresce antes de agendar: número que entrou hoje já pode ter
        // um colega para conversar amanhã.
        await GrowGraphAsync(db, now, settings, ct);

        var peers = await EligiblePeersAsync(db, ct);
        if (peers.Count < 2)
            return 0;

        var calendar = await reports.BuildCalendarAsync(ct);
        var businessHours = calendar.BusinessTimeBetween(now, now.AddMinutes(1)) > TimeSpan.Zero;
        if (!WarmupPlan.IsSendableMoment(clock.LocalNow(now), businessHours, settings, random))
            return 0;

        var links = await db.Set<WarmupLink>().AsNoTracking().ToListAsync(ct);
        var today = clock.Today(now);
        var dayStart = clock.DayStartUtc(now);
        var created = 0;

        foreach (var peer in peers)
        {
            var peerLinks = links.Where(l => l.PeerAId == peer.Id || l.PeerBId == peer.Id).ToList();
            if (peerLinks.Count == 0)
                continue;

            // Uma conversa por vez por número: duas em paralelo com colegas
            // diferentes, no mesmo minuto, é o padrão de robô.
            var busy = await db.Set<WarmupConversation>().AnyAsync(c =>
                (c.PeerAId == peer.Id || c.PeerBId == peer.Id)
                && (c.Status == WarmupConversationStatus.Scheduled || c.Status == WarmupConversationStatus.Running), ct);
            if (busy)
                continue;

            var target = WarmupPlan.TargetFor(
                peer.Id, today, peerLinks.Count,
                await RealMessagesTodayAsync(db, peer.WhatsappNumberId, dayStart, ct),
                await WarmupMessagesTodayAsync(db, peer.Id, dayStart, ct),
                settings);

            if (target.Deficit <= 0)
                continue;

            var link = PickLink(peerLinks, now);
            if (link is null)
                continue;

            var other = peers.FirstOrDefault(p => p.Id == (link.PeerAId == peer.Id ? link.PeerBId : link.PeerAId));
            if (other is null)
                continue;

            var turns = WarmupPlan.TurnsFor(target.Deficit, settings, random);
            if (turns < settings.MinTurnsPerConversation)
                continue;

            var outcome = await generator.GenerateAsync(peer.Persona, other.Persona, turns, ct);
            if (outcome.Conversation is null)
            {
                // Uma falha para a passada inteira: se a IA recusou uma conversa,
                // vai recusar as outras cinco do mesmo ciclo pelo mesmo motivo.
                await state.PauseGenerationAsync(db, outcome.Error ?? "Falha desconhecida na geração.", ct);
                return created;
            }

            await state.ClearGenerationPauseAsync(db, ct);
            CreateConversation(db, outcome.Conversation, link, peer, other, now, settings);
            await db.SaveChangesAsync(ct);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    private void CreateConversation(
        AppDbContext db, GeneratedConversation content, WarmupLink link,
        WarmupPeer a, WarmupPeer b, DateTime now, WarmupOptions settings)
    {
        var conversation = new WarmupConversation
        {
            Id = Guid.NewGuid(),
            LinkId = link.Id,
            PeerAId = a.Id,
            PeerBId = b.Id,
            Theme = content.Theme,
            Status = WarmupConversationStatus.Scheduled,
            CreatedAt = now,
        };
        db.Add(conversation);

        // Abandono deliberado: conversa que morre no meio é comum, e
        // reciprocidade perfeita é anomalia.
        var abandon = random.NextDouble() < settings.AbandonChance;
        var keep = abandon && content.Turns.Count > 2
            ? Math.Max(2, content.Turns.Count - 1 - (int)(random.NextDouble() * 2))
            : content.Turns.Count;

        var at = now;
        for (var i = 0; i < keep; i++)
        {
            at = at.Add(WarmupPlan.GapBetweenTurns(settings, random));
            db.Add(new WarmupTurn
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Sequence = i + 1,
                FromPeerId = content.Turns[i].FromA ? a.Id : b.Id,
                Text = content.Turns[i].Text,
                ScheduledAt = at,
            });
        }

        logger.LogInformation("Aquecimento: conversa de {Turns} turnos agendada sobre {Theme}.", keep, content.Theme);
    }

    // Prefere o par que está há mais tempo sem falar, ponderado pela intensidade
    // daquela relação — o núcleo fala quase todo dia, o ocasional some por
    // semanas, e o raro é a cauda.
    private WarmupLink? PickLink(List<WarmupLink> links, DateTime now)
    {
        WarmupLink? best = null;
        var bestScore = double.MinValue;

        foreach (var link in links)
        {
            var daysSince = link.LastConversationAt is { } last
                ? (now - last).TotalDays
                : 30;
            var expectedGap = 7.0 / Math.Max(0.05, link.ConversationsPerWeek);

            // Só entra na disputa quem já passou do intervalo esperado; o ruído
            // evita que a ordem seja sempre a mesma.
            var score = daysSince / expectedGap + random.NextDouble() * 0.3;
            if (score < 1)
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                best = link;
            }
        }

        return best;
    }

    private static async Task<List<WarmupPeer>> EligiblePeersAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Número banido, em cooldown, com envio pausado ou fora do ar sai do
        // pool sozinho — e volta sozinho quando normalizar.
        return await db.Set<WarmupPeer>()
            .Where(p => p.LeftAt == null)
            .Join(db.Set<WhatsappNumber>().AsNoTracking(), p => p.WhatsappNumberId, n => n.Id, (p, n) => new { p, n })
            .Where(x => x.n.Status == NumberStatus.Active)
            .Where(x => x.n.BannedUntil == null || x.n.BannedUntil < now)
            .Where(x => x.n.SendingPausedUntil == null || x.n.SendingPausedUntil < now)
            .Select(x => x.p)
            .ToListAsync(ct);
    }

    private static Task<int> RealMessagesTodayAsync(AppDbContext db, Guid numberId, DateTime dayStart, CancellationToken ct) =>
        db.Set<Message>().CountAsync(m =>
            m.WhatsappNumberId == numberId
            && m.Direction == MessageDirection.Outbound
            && m.Timestamp >= dayStart, ct);

    private static Task<int> WarmupMessagesTodayAsync(AppDbContext db, Guid peerId, DateTime dayStart, CancellationToken ct) =>
        db.Set<WarmupTurn>().CountAsync(t =>
            t.FromPeerId == peerId && t.SentAt != null && t.SentAt >= dayStart, ct);

    private async Task GrowGraphAsync(AppDbContext db, DateTime now, WarmupOptions settings, CancellationToken ct)
    {
        var peers = await db.Set<WarmupPeer>().AsNoTracking()
            .Where(p => p.LeftAt == null)
            .Select(p => new GraphPeer(p.Id, p.JoinedAt))
            .ToListAsync(ct);

        var existing = await db.Set<WarmupLink>().AsNoTracking()
            .Select(l => new GraphLink(l.PeerAId, l.PeerBId, l.Kind))
            .ToListAsync(ct);

        foreach (var link in WarmupGraph.Grow(peers, existing, now, settings, random))
        {
            db.Add(new WarmupLink
            {
                Id = Guid.NewGuid(),
                PeerAId = link.PeerAId,
                PeerBId = link.PeerBId,
                Kind = link.Kind,
                ConversationsPerWeek = link.ConversationsPerWeek,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
