using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Warmup;

public record WarmupToggleRequest(bool Enabled);

public record WarmupPeerRequest(Guid NumberId);

public record WarmupPeerDto(
    Guid? PeerId, Guid NumberId, string Phone, string SellerName, string NumberStatus,
    bool InPool, string? IneligibleReason, string? Persona,
    int CoreCircle, int OccasionalCircle, IReadOnlyList<string> Circle,
    int Goal, int EffectiveGoal, bool CappedByGraph,
    int RealMessagesToday, int WarmupMessagesToday);

public record WarmupTurnDto(int Sequence, string FromPhone, string Text, DateTime ScheduledAt, DateTime? SentAt, bool Delivered);

public record WarmupConversationDto(
    Guid Id, string Theme, string Status, string PhoneA, string PhoneB,
    DateTime CreatedAt, DateTime? CompletedAt, bool Archived, IReadOnlyList<WarmupTurnDto> Turns);

public record WarmupOverviewDto(
    bool Enabled, DateTime? HaltedAt, string? HaltReason, string? IdleReason,
    int PeersInPool, int MessagesToday, int ConversationsToday, double? DeliveryRate,
    IReadOnlyList<WarmupPeerDto> Numbers, IReadOnlyList<WarmupConversationDto> Conversations);

public static class WarmupEndpoints
{
    public static RouteGroupBuilder MapWarmupEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/warmup", async (WarmupQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.OverviewAsync(ct)));

        group.MapPut("/warmup/settings", async (WarmupToggleRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var settings = await db.Set<WarmupSettings>().FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);
            if (settings is null)
            {
                settings = new WarmupSettings { Id = WarmupSettings.SingletonId };
                db.Add(settings);
            }

            settings.Enabled = request.Enabled;
            // Religar limpa o kill switch: é a decisão manual que ele exige.
            if (request.Enabled)
            {
                settings.HaltedAt = null;
                settings.HaltReason = null;
            }

            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new WarmupToggleRequest(settings.Enabled));
        });

        // Botão de pânico: para tudo agora, sem desligar o interruptor.
        group.MapPost("/warmup/halt", async (IWarmupState state, AppDbContext db, CancellationToken ct) =>
        {
            await state.HaltAsync(db, "Parado manualmente pelo operador.", ct);
            return Results.NoContent();
        });

        group.MapPost("/warmup/peers", async (
            WarmupPeerRequest request, AppDbContext db, IOptions<WarmupOptions> options, CancellationToken ct) =>
        {
            if (!await db.Set<WhatsappNumber>().AnyAsync(n => n.Id == request.NumberId, ct))
                return Results.NotFound();

            var existing = await db.Set<WarmupPeer>().FirstOrDefaultAsync(p => p.WhatsappNumberId == request.NumberId, ct);
            if (existing is not null)
            {
                // Voltar ao pool reencontra o mesmo círculo: a relação existia
                // antes e não some porque o número saiu por uns dias.
                existing.LeftAt = null;
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            }

            // Persona sorteada e fixa: sem ela os dois lados de um par soariam
            // como a mesma pessoa.
            var personas = Enum.GetValues<WarmupPersona>();
            db.Add(new WarmupPeer
            {
                Id = Guid.NewGuid(),
                WhatsappNumberId = request.NumberId,
                Persona = personas[Math.Abs(request.NumberId.GetHashCode()) % personas.Length],
                JoinedAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Sair não apaga histórico nem arestas.
        group.MapDelete("/warmup/peers/{numberId:guid}", async (Guid numberId, AppDbContext db, CancellationToken ct) =>
        {
            var updated = await db.Set<WarmupPeer>()
                .Where(p => p.WhatsappNumberId == numberId && p.LeftAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, DateTime.UtcNow), ct);

            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });

        return group;
    }
}

public sealed class WarmupQueries(AppDbContext db, IOptions<WarmupOptions> options, WarmupClock clock)
{
    public async Task<WarmupOverviewDto> OverviewAsync(CancellationToken ct)
    {
        var settings = await db.Set<WarmupSettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);

        var now = DateTime.UtcNow;
        var today = clock.Today(now);
        var dayStart = clock.DayStartUtc(now);

        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id, (n, s) => new { n, SellerName = s.Name })
            .OrderBy(x => x.SellerName).ThenBy(x => x.n.Phone)
            .ToListAsync(ct);

        var peers = await db.Set<WarmupPeer>().AsNoTracking().ToListAsync(ct);
        var links = await db.Set<WarmupLink>().AsNoTracking().ToListAsync(ct);
        var phoneByPeer = peers.ToDictionary(
            p => p.Id,
            p => numbers.FirstOrDefault(n => n.n.Id == p.WhatsappNumberId)?.n.Phone ?? "—");

        var realToday = await db.Set<Message>().AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound && m.Timestamp >= dayStart)
            .GroupBy(m => m.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var warmupToday = await db.Set<WarmupTurn>().AsNoTracking()
            .Where(t => t.SentAt != null && t.SentAt >= dayStart)
            .GroupBy(t => t.FromPeerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var rows = numbers.Select(x =>
        {
            var peer = peers.FirstOrDefault(p => p.WhatsappNumberId == x.n.Id && p.LeftAt == null);
            var peerLinks = peer is null ? [] : links.Where(l => l.PeerAId == peer.Id || l.PeerBId == peer.Id).ToList();

            var real = realToday.GetValueOrDefault(x.n.Id);
            var warm = peer is null ? 0 : warmupToday.GetValueOrDefault(peer.Id);
            var target = peer is null
                ? new DailyTarget(0, 0, 0, false)
                : WarmupPlan.TargetFor(peer.Id, today, peerLinks.Count, real, warm, options.Value);

            return new WarmupPeerDto(
                peer?.Id, x.n.Id, x.n.Phone, x.SellerName, x.n.Status.ToString(),
                InPool: peer is not null,
                IneligibleReason: Ineligible(x.n, now),
                Persona: peer?.Persona.ToString(),
                CoreCircle: peerLinks.Count(l => l.Kind == WarmupLinkKind.Core),
                OccasionalCircle: peerLinks.Count(l => l.Kind != WarmupLinkKind.Core),
                Circle: [.. peerLinks.Select(l => phoneByPeer.GetValueOrDefault(l.PeerAId == peer!.Id ? l.PeerBId : l.PeerAId, "—"))],
                target.Goal, target.EffectiveGoal, target.CappedByGraph,
                real, warm);
        }).ToList();

        var conversations = await db.Set<WarmupConversation>().AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        var conversationIds = conversations.Select(c => c.Id).ToList();
        var turns = await db.Set<WarmupTurn>().AsNoTracking()
            .Where(t => conversationIds.Contains(t.ConversationId))
            .OrderBy(t => t.Sequence)
            .ToListAsync(ct);

        var sample = turns.Where(t => t.SentAt != null && t.SentAt <= now.AddMinutes(-15)).ToList();

        return new WarmupOverviewDto(
            Enabled: settings?.Enabled ?? false,
            HaltedAt: settings?.HaltedAt,
            HaltReason: settings?.HaltReason,
            IdleReason: IdleReason(settings, rows, now),
            PeersInPool: peers.Count(p => p.LeftAt == null),
            MessagesToday: warmupToday.Values.Sum(),
            ConversationsToday: conversations.Count(c => c.CreatedAt >= dayStart),
            DeliveryRate: sample.Count == 0 ? null : (double)sample.Count(t => t.DeliveredAt is not null) / sample.Count,
            Numbers: rows,
            Conversations: [.. conversations.Select(c => new WarmupConversationDto(
                c.Id, c.Theme, c.Status.ToString(),
                phoneByPeer.GetValueOrDefault(c.PeerAId, "—"),
                phoneByPeer.GetValueOrDefault(c.PeerBId, "—"),
                c.CreatedAt, c.CompletedAt, c.ArchivedA && c.ArchivedB,
                [.. turns.Where(t => t.ConversationId == c.Id).Select(t => new WarmupTurnDto(
                    t.Sequence, phoneByPeer.GetValueOrDefault(t.FromPeerId, "—"), t.Text,
                    t.ScheduledAt, t.SentAt, t.DeliveredAt is not null))]))]);
    }

    // Por que NENHUMA conversa está sendo agendada agora, mesmo com tudo ligado.
    // Silêncio sem explicação é indistinguível de feature quebrada — foi
    // exatamente assim que o aquecimento pareceu morto no primeiro teste real.
    private string? IdleReason(WarmupSettings? settings, List<WarmupPeerDto> rows, DateTime now)
    {
        // Desligado e parado já têm o próprio aviso, bem mais visível.
        if (settings is not { Enabled: true } || settings.HaltedAt is not null)
            return null;

        var pool = rows.Where(r => r.InPool && r.IneligibleReason is null).ToList();
        if (pool.Count < 2)
            return "Menos de dois números elegíveis no pool: não há com quem conversar.";

        var opts = options.Value;
        var local = clock.LocalNow(now);
        if (local.Hour < opts.MorningFromHour || local.Hour >= opts.EveningUntilHour)
            return $"Fora da janela de envio ({opts.MorningFromHour}h às {opts.EveningUntilHour}h). "
                + "Nada sai de madrugada porque colega nenhum conversa a essa hora.";

        if (pool.Any(p => p.CoreCircle + p.OccasionalCircle == 0))
            return "O círculo ainda não foi montado. Ele nasce na primeira passada do agendador, "
                + "em até dois minutos com o aquecimento ligado.";

        if (pool.All(p => p.RealMessagesToday + p.WarmupMessagesToday >= p.EffectiveGoal))
            return "Todos os números já cobriram a meta de hoje — a maior parte com conversa real "
                + "de cliente, que é justamente o que o aquecimento existe para completar. "
                + "Com poucos números no pool a meta é baixa, então o tráfego real a cobre sozinho.";

        return null;
    }

    // Por que este número não pode participar agora. A tela mostra o motivo —
    // "fora do pool" sem explicação vira chamado de suporte.
    private static string? Ineligible(WhatsappNumber number, DateTime now)
    {
        if (number.Status != NumberStatus.Active)
            return "número não está conectado";
        if (number.BannedUntil is { } banned && banned > now)
            return "em cooldown pós-ban";
        if (number.SendingPausedUntil is { } paused && paused > now)
            return "envio pausado pelo WhatsApp";
        return null;
    }
}
