using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Proxies;

public sealed record ProxyNumberDto(Guid NumberId, string Phone, string SellerName, string Status);

public sealed record ProxyDto(
    Guid Id,
    string ShortId,
    string Label,
    string Kind,
    string Host,
    int Port,
    string Status,
    int NumbersCount,
    int Capacity,
    int SellersCount,
    int BansCount,
    int BannedNumbersCount,
    DateTime? ExpiresAt,
    DateTime? LastTestedAt,
    bool? LastTestOk,
    IReadOnlyList<ProxyNumberDto> Numbers);

public sealed record ProxyOverviewDto(
    bool Enabled,
    int ActiveProxies,
    int AssignedNumbers,
    int NumbersWithoutProxy,
    int BansInPeriod,
    IReadOnlyList<ProxyDto> Proxies,
    IReadOnlyList<ProxyNumberDto> Unassigned);

// Leitura da tela de proxies. As contagens de ban respeitam a JANELA DE
// ATRIBUIÇÃO: o ban de julho fica com o proxy que valia em julho, mesmo que o
// número tenha mudado de proxy depois. Sem isso, trocar um número de proxy
// reescreveria o passado e a estatística que decide trocar de fornecedor viraria
// ficção. A senha do proxy NUNCA sai daqui.
public sealed class ProxyQueries(AppDbContext db, IProxySwitch proxySwitch, IOptions<ProxyOptions> options)
{
    public async Task<ProxyOverviewDto> OverviewAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var proxies = await db.Set<Proxy>().AsNoTracking().OrderBy(p => p.Label).ToListAsync(ct);

        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id, (n, s) => new { n, SellerName = s.Name })
            .GroupJoin(
                db.Set<NumberProxyAssignment>().AsNoTracking().Where(a => a.ReleasedAt == null),
                x => x.n.Id, a => a.WhatsappNumberId,
                (x, assignments) => new { x.n, x.SellerName, Assignments = assignments })
            .SelectMany(x => x.Assignments.DefaultIfEmpty(), (x, a) => new
            {
                x.n.Id,
                x.n.Phone,
                x.n.Status,
                x.SellerName,
                x.n.SellerId,
                ProxyId = a != null ? (Guid?)a.ProxyId : null,
            })
            .OrderBy(x => x.SellerName).ThenBy(x => x.Phone)
            .ToListAsync(ct);

        // Bans do período, atribuídos ao proxy vigente no INSTANTE do evento.
        var bans = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.OccurredAt >= fromUtc && e.OccurredAt < toUtc && e.StatusReason == 403)
            .Join(db.Set<NumberProxyAssignment>().AsNoTracking(),
                e => e.WhatsappNumberId, a => a.WhatsappNumberId,
                (e, a) => new { e.WhatsappNumberId, e.OccurredAt, a.ProxyId, a.AssignedAt, a.ReleasedAt })
            .Where(x => x.AssignedAt <= x.OccurredAt && (x.ReleasedAt == null || x.OccurredAt < x.ReleasedAt))
            .ToListAsync(ct);

        var defaultCapacity = options.Value.DefaultCapacity;

        var items = proxies.Select(p =>
        {
            var mine = numbers.Where(n => n.ProxyId == p.Id).ToList();
            var proxyBans = bans.Where(b => b.ProxyId == p.Id).ToList();

            return new ProxyDto(
                p.Id, p.ShortId, p.Label, p.Kind.ToString(), p.Host, p.Port, p.Status.ToString(),
                NumbersCount: mine.Count,
                Capacity: p.CapacityOr(defaultCapacity),
                SellersCount: mine.Select(n => n.SellerId).Distinct().Count(),
                BansCount: proxyBans.Count,
                BannedNumbersCount: proxyBans.Select(b => b.WhatsappNumberId).Distinct().Count(),
                p.ExpiresAt, p.LastTestedAt, p.LastTestOk,
                Numbers: [.. mine.Select(n => new ProxyNumberDto(n.Id, n.Phone, n.SellerName, n.Status.ToString()))]);
        }).ToList();

        // Número banido permanentemente não conta como "sem proxy": ele não
        // disputa vaga, então listá-lo aqui pediria proxy que ninguém vai usar.
        var unassigned = numbers
            .Where(n => n.ProxyId is null && n.Status != NumberStatus.BannedPermanent)
            .Select(n => new ProxyNumberDto(n.Id, n.Phone, n.SellerName, n.Status.ToString()))
            .ToList();

        return new ProxyOverviewDto(
            Enabled: await proxySwitch.IsEnabledAsync(db, ct),
            ActiveProxies: proxies.Count(p => p.Status == ProxyStatus.Active),
            AssignedNumbers: numbers.Count(n => n.ProxyId is not null),
            NumbersWithoutProxy: unassigned.Count,
            BansInPeriod: bans.Count,
            Proxies: items,
            Unassigned: unassigned);
    }
}
