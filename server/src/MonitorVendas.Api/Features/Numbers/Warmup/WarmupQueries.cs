using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Numbers.Warmup;

public sealed record WarmupNumberDto(
    Guid NumberId,
    string Phone,
    string SellerName,
    string NumberStatus,
    string State,
    int Day,
    int TotalDays,
    int? MessagesPerDay,
    int MessagesToday,
    int NewContactsPerDay,
    int NewContactsToday,
    DateTime? StartedAt,
    DateTime? PausedAt,
    DateTime? CompletedAt)
{
    // Bateu o teto: é a resposta visual para "por que este número parou de
    // enviar?", que hoje é a pergunta sem resposta na tela.
    public bool AtCeiling => MessagesPerDay is { } cap && MessagesToday >= cap;
}

public sealed record WarmupCurveStepDto(int ThroughDay, int MessagesPerDay, int NewContactsPerDay);

public sealed record WarmupOverviewDto(
    bool Enabled,
    int Warming,
    int Mature,
    int AtCeiling,
    IReadOnlyList<WarmupCurveStepDto> Curve,
    IReadOnlyList<WarmupNumberDto> Numbers);

// Leitura da tela de aquecimento. Tudo já está no banco — é agregação em lote,
// não coleta nova.
public sealed class WarmupQueries(AppDbContext db, IOptions<WarmupOptions> options)
{
    public async Task<WarmupOverviewDto> OverviewAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = DateTime.UtcNow;
        var today = now.Date;

        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id, (n, s) => new { n, SellerName = s.Name })
            .OrderBy(x => x.SellerName).ThenBy(x => x.n.Phone)
            .ToListAsync(ct);

        // Conta TUDO que saiu pelo número hoje, inclusive o que o vendedor
        // mandou pelo celular: é assim que o WhatsApp conta, e mostrar só o que
        // o sistema enviou daria uma falsa folga.
        var sentToday = await db.Set<Message>().AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound && m.Timestamp >= today)
            .GroupBy(m => m.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var newContactsToday = await db.Set<Conversation>().AsNoTracking()
            .Where(c => !c.StartedByContact && c.StartedAt >= today)
            .GroupBy(c => c.WhatsappNumberId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var totalDays = WarmupPolicy.TotalDays(settings);

        var items = numbers.Select(x =>
        {
            var limits = WarmupPolicy.LimitsFor(
                x.n.WarmupStartedAt, now, settings, x.n.WarmupPausedAt, x.n.WarmupCompletedAt);

            return new WarmupNumberDto(
                x.n.Id, x.n.Phone, x.SellerName, x.n.Status.ToString(),
                limits.State.ToString(), limits.Day, totalDays,
                limits.MessagesPerDay, sentToday.GetValueOrDefault(x.n.Id),
                limits.NewContactsPerDay, newContactsToday.GetValueOrDefault(x.n.Id),
                x.n.WarmupStartedAt, x.n.WarmupPausedAt, x.n.WarmupCompletedAt);
        }).ToList();

        return new WarmupOverviewDto(
            Enabled: settings.Enabled,
            Warming: items.Count(i => i.State is nameof(WarmupState.Warming) or nameof(WarmupState.Paused)),
            Mature: items.Count(i => i.State == nameof(WarmupState.Mature)),
            AtCeiling: items.Count(i => i.AtCeiling),
            Curve: [.. settings.Curve.OrderBy(s => s.ThroughDay)
                .Select(s => new WarmupCurveStepDto(s.ThroughDay, s.MessagesPerDay, s.NewContactsPerDay))],
            Numbers: items);
    }
}
