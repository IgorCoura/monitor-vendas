using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Proxies;

// Decide por qual proxy uma instância nasce. Existe porque a instância é criada
// em CINCO lugares (pareamento, código do pareamento, código da reconexão,
// reparo de instância sumida e o cadastro legado por telefone), e um que
// esquecesse o proxy criaria um número saindo pelo IP do servidor em silêncio.
// Por isso o parâmetro de proxy no CreateInstanceAsync é obrigatório: o
// compilador força cada chamador a decidir, e passar null é escolha explícita.
public sealed class ProxyResolver(
    AppDbContext db,
    IProxySwitch proxySwitch,
    IOptions<ProxyOptions> options,
    ILogger<ProxyResolver> logger)
{
    // Escolhe (sem gravar nada) o proxy de um número que ainda não existe.
    public async Task<Guid?> ChooseForNewNumberAsync(Guid sellerId, CancellationToken ct)
    {
        if (!await proxySwitch.IsEnabledAsync(db, ct))
            return null;

        var target = Guid.NewGuid();
        var plan = await PlanAsync([new AllocatableNumber(target, sellerId, DateTime.UtcNow, null)], allowMoves: false, ct);
        var assignment = plan.Assignments.FirstOrDefault(a => a.NumberId == target);

        if (assignment is null)
            logger.LogWarning("Nenhum proxy com vaga para o vendedor {SellerId}: o número nasce sem proxy.", sellerId);

        return assignment?.ProxyId;
    }

    public async Task<ProxyCredentials?> CredentialsForAsync(Guid proxyId, CancellationToken ct)
    {
        var proxy = await db.Set<Proxy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == proxyId, ct);
        return Credentials(proxy);
    }

    // Proxy vigente de um número já cadastrado — para quando a instância dele é
    // RECRIADA (código de pareamento, reparo de instância sumida).
    public async Task<ProxyCredentials?> CredentialsForNumberAsync(Guid numberId, CancellationToken ct)
    {
        if (!await proxySwitch.IsEnabledAsync(db, ct))
            return null;

        var proxy = await db.Set<NumberProxyAssignment>().AsNoTracking()
            .Where(a => a.WhatsappNumberId == numberId && a.ReleasedAt == null)
            .Join(db.Set<Proxy>().AsNoTracking(), a => a.ProxyId, p => p.Id, (_, p) => p)
            .FirstOrDefaultAsync(ct);

        return Credentials(proxy);
    }

    // Registra a atribuição de um número recém-criado. `applied: true` quando a
    // instância JÁ nasceu com estes campos — não há o que aplicar depois.
    public async Task AssignAsync(Guid numberId, Guid proxyId, bool applied, CancellationToken ct)
    {
        var exists = await db.Set<NumberProxyAssignment>()
            .AnyAsync(a => a.WhatsappNumberId == numberId && a.ReleasedAt == null, ct);
        if (exists)
            return;

        db.Add(new NumberProxyAssignment
        {
            Id = Guid.NewGuid(),
            WhatsappNumberId = numberId,
            ProxyId = proxyId,
            AssignedAt = DateTime.UtcNow,
            Reason = ProxyAssignmentReason.Auto,
            AppliedAt = applied ? DateTime.UtcNow : null,
        });
    }

    // Escolhe e registra de uma vez, para quem cria número fora do pareamento.
    public async Task<Guid?> AssignNewAsync(Guid numberId, Guid sellerId, bool applied, CancellationToken ct)
    {
        var chosen = await ChooseForNewNumberAsync(sellerId, ct);
        if (chosen is { } proxyId)
            await AssignAsync(numberId, proxyId, applied, ct);

        return chosen;
    }

    // A Evolution recusou estas credenciais: tira o proxy da fila para o
    // próximo número não bater na mesma parede.
    public async Task MarkFailedAsync(Guid proxyId, string error, CancellationToken ct)
    {
        await db.Set<Proxy>()
            .Where(p => p.Id == proxyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, ProxyStatus.Failed)
                .SetProperty(p => p.LastTestOk, false)
                .SetProperty(p => p.LastTestedAt, DateTime.UtcNow), ct);

        logger.LogWarning("Proxy {ProxyId} marcado como falho: {Error}", proxyId, error);
    }

    // O plano completo, com a carga atual de todos os proxies. Compartilhado
    // entre a escolha de um número só e a prévia de redistribuição, para os dois
    // nunca divergirem.
    public async Task<AllocationPlan> PlanAsync(
        IReadOnlyList<AllocatableNumber> extra,
        bool allowMoves,
        CancellationToken ct)
    {
        var proxies = await db.Set<Proxy>().AsNoTracking().ToListAsync(ct);
        if (proxies.Count == 0)
            return new AllocationPlan([], [.. extra.Select(n => n.Id)]);

        var current = await db.Set<WhatsappNumber>().AsNoTracking()
            .GroupJoin(
                db.Set<NumberProxyAssignment>().AsNoTracking().Where(a => a.ReleasedAt == null),
                n => n.Id, a => a.WhatsappNumberId,
                (n, assignments) => new { n.Id, n.SellerId, n.CreatedAt, n.Status, Assignments = assignments })
            .SelectMany(x => x.Assignments.DefaultIfEmpty(), (x, a) => new
            {
                x.Id,
                x.SellerId,
                x.CreatedAt,
                x.Status,
                ProxyId = a != null ? (Guid?)a.ProxyId : null,
            })
            .ToListAsync(ct);

        var bans = await RecentBansAsync(ct);
        var defaultCapacity = options.Value.DefaultCapacity;

        var allocatable = proxies
            .Select(p => new AllocatableProxy(p.Id, p.CapacityOr(defaultCapacity), p.AcceptsNewNumbers, bans.GetValueOrDefault(p.Id), p.CreatedAt))
            .ToList();

        // Número banido permanentemente não disputa vaga: ocupar um IP com quem
        // não volta seria desperdiçar capacidade contratada.
        var numbers = current
            .Where(x => x.Status != NumberStatus.BannedPermanent)
            .Select(x => new AllocatableNumber(x.Id, x.SellerId, x.CreatedAt, x.ProxyId))
            .Concat(extra)
            .ToList();

        return ProxyAllocator.Allocate(numbers, allocatable, new AllocationOptions(allowMoves));
    }

    private async Task<Dictionary<Guid, int>> RecentBansAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-90);

        return await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.OccurredAt >= since && e.StatusReason == 403)
            .Join(db.Set<NumberProxyAssignment>().AsNoTracking(),
                e => e.WhatsappNumberId, a => a.WhatsappNumberId,
                (e, a) => new { e.OccurredAt, a.ProxyId, a.AssignedAt, a.ReleasedAt })
            .Where(x => x.AssignedAt <= x.OccurredAt && (x.ReleasedAt == null || x.OccurredAt < x.ReleasedAt))
            .GroupBy(x => x.ProxyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    private ProxyCredentials? Credentials(Proxy? proxy)
    {
        if (proxy is null)
            return null;

        // socks5 é o que o Baileys quer; se o plano não expõe a porta socks,
        // cai na HTTP em vez de mandar uma porta que não existe.
        var socks = string.Equals(options.Value.Protocol, "socks5", StringComparison.OrdinalIgnoreCase)
            && proxy.SocksPort is > 0;

        return new ProxyCredentials(
            proxy.Host,
            socks ? proxy.SocksPort!.Value : proxy.Port,
            socks ? "socks5" : "http",
            proxy.Username,
            proxy.Password);
    }
}
