namespace MonitorVendas.Api.Features.Proxies;

public sealed record AllocatableNumber(Guid Id, Guid SellerId, DateTime CreatedAt, Guid? CurrentProxyId);

public sealed record AllocatableProxy(Guid Id, int Capacity, bool AcceptsNewNumbers, int RecentBans, DateTime CreatedAt);

public sealed record ProxyAssignmentPlan(Guid NumberId, Guid? FromProxyId, Guid ProxyId);

// `Unassigned` são os números que ficaram sem proxy por falta de vaga: a tela
// mostra e o operador compra mais. Nunca estouramos a capacidade em silêncio.
public sealed record AllocationPlan(
    IReadOnlyList<ProxyAssignmentPlan> Assignments,
    IReadOnlyList<Guid> Unassigned);

public sealed record AllocationOptions(bool AllowMoves = false);

// Escolhe o proxy de cada número. Puro de propósito, no espírito do
// MetricsCalculator: entrada e saída são listas simples, então cada regra é
// provada por teste unitário rápido, sem Postgres.
//
// O custo é LEXICOGRÁFICO, não uma soma ponderada — peso é número inventado, não
// se explica para quem opera e não se testa direito. Assim a regra se lê em
// português: "primeiro evita concentrar o vendedor; entre os que empatam, o
// menos carregado; entre esses, o com menos ban".
public static class ProxyAllocator
{
    public static AllocationPlan Allocate(
        IReadOnlyList<AllocatableNumber> numbers,
        IReadOnlyList<AllocatableProxy> proxies,
        AllocationOptions options)
    {
        var candidates = proxies.Where(p => p.AcceptsNewNumbers).ToList();

        // Carga corrente: números que JÁ estão em cada proxy contam desde o
        // início, senão o balanceamento ignoraria o que existe hoje.
        var load = candidates.ToDictionary(p => p.Id, _ => 0);
        var sellersOn = candidates.ToDictionary(p => p.Id, _ => new Dictionary<Guid, int>());

        foreach (var number in numbers)
        {
            if (number.CurrentProxyId is not { } current || !load.ContainsKey(current))
                continue;

            // Sem AllowMoves o número fica onde está: trocar de IP custa restart
            // de socket e é fator de risco de ban. Rebalancear é ação humana.
            if (!options.AllowMoves)
                Occupy(load, sellersOn, current, number.SellerId);
        }

        var assignments = new List<ProxyAssignmentPlan>();
        var unassigned = new List<Guid>();

        // Vendedor com mais números escolhe antes ("hardest first"): quem tem 1
        // cabe em qualquer lugar, quem tem 5 é quem tem restrição de verdade.
        var pending = numbers
            .Where(n => options.AllowMoves || n.CurrentProxyId is null || !load.ContainsKey(n.CurrentProxyId.Value))
            .GroupBy(n => n.SellerId)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .SelectMany(g => g.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id));

        foreach (var number in pending)
        {
            var chosen = Choose(candidates, load, sellersOn, number.SellerId);
            if (chosen is null)
            {
                unassigned.Add(number.Id);
                continue;
            }

            if (number.CurrentProxyId == chosen.Id)
            {
                Occupy(load, sellersOn, chosen.Id, number.SellerId);
                continue;
            }

            assignments.Add(new ProxyAssignmentPlan(number.Id, number.CurrentProxyId, chosen.Id));
            Occupy(load, sellersOn, chosen.Id, number.SellerId);
        }

        return new AllocationPlan(assignments, unassigned);
    }

    private static AllocatableProxy? Choose(
        List<AllocatableProxy> candidates,
        Dictionary<Guid, int> load,
        Dictionary<Guid, Dictionary<Guid, int>> sellersOn,
        Guid sellerId)
    {
        AllocatableProxy? best = null;
        var bestKey = (int.MaxValue, int.MaxValue, int.MaxValue, DateTime.MaxValue, Guid.Empty);

        foreach (var proxy in candidates)
        {
            if (load[proxy.Id] >= proxy.Capacity)
                continue;

            var key = (
                sellersOn[proxy.Id].GetValueOrDefault(sellerId),  // 1º: espalhar o vendedor
                load[proxy.Id],                                   // 2º: equilibrar a carga
                proxy.RecentBans,                                 // 3º: desempatar contra o queimado
                proxy.CreatedAt, proxy.Id);                       // 4º: determinismo

            if (best is null || Compare(key, bestKey) < 0)
            {
                best = proxy;
                bestKey = key;
            }
        }

        return best;
    }

    private static int Compare(
        (int Seller, int Load, int Bans, DateTime CreatedAt, Guid Id) a,
        (int Seller, int Load, int Bans, DateTime CreatedAt, Guid Id) b)
    {
        if (a.Seller != b.Seller) return a.Seller.CompareTo(b.Seller);
        if (a.Load != b.Load) return a.Load.CompareTo(b.Load);
        if (a.Bans != b.Bans) return a.Bans.CompareTo(b.Bans);
        if (a.CreatedAt != b.CreatedAt) return a.CreatedAt.CompareTo(b.CreatedAt);
        return a.Id.CompareTo(b.Id);
    }

    private static void Occupy(
        Dictionary<Guid, int> load,
        Dictionary<Guid, Dictionary<Guid, int>> sellersOn,
        Guid proxyId,
        Guid sellerId)
    {
        load[proxyId]++;
        var sellers = sellersOn[proxyId];
        sellers[sellerId] = sellers.GetValueOrDefault(sellerId) + 1;
    }
}
