using MonitorVendas.Api.Features.Proxies;

namespace MonitorVendas.Tests.Proxies;

public class ProxyAllocatorTests
{
    private static readonly DateTime Day = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static AllocatableProxy Proxy(int n, int capacity = 2, int bans = 0, bool accepts = true) =>
        new(Id(n), capacity, accepts, bans, Day.AddMinutes(n));

    private static AllocatableNumber Number(int n, int seller, Guid? current = null) =>
        new(Id(100 + n), Id(900 + seller), Day.AddMinutes(n), current);

    // Com proxies sobrando, os números de um mesmo vendedor vão para proxies
    // DIFERENTES: se um IP queimar, o vendedor não fica inteiro fora do ar.
    [Fact]
    public void SpreadsOneSellerAcrossProxies()
    {
        var proxies = new[] { Proxy(1), Proxy(2), Proxy(3) };
        var numbers = new[] { Number(1, 1), Number(2, 1), Number(3, 1) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Empty(plan.Unassigned);
        Assert.Equal(3, plan.Assignments.Select(a => a.ProxyId).Distinct().Count());
    }

    // Quando o vendedor não é restrição (nenhum proxy tem número dele), a decisão
    // cai no balanceamento puro.
    [Fact]
    public void BalancesLoadWhenSellerIsNotAConstraint()
    {
        var proxies = new[] { Proxy(1), Proxy(2) };
        var numbers = new[] { Number(1, 1), Number(2, 2), Number(3, 3), Number(4, 4) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        var perProxy = plan.Assignments.GroupBy(a => a.ProxyId).Select(g => g.Count()).OrderBy(c => c);
        Assert.Equal([2, 2], perProxy);
    }

    // O exemplo do plano: Ana(3), Bruno(2), Carla(1) em 4 proxies de capacidade 3.
    // Carga 2/2/1/1 e nenhum vendedor concentrado.
    [Fact]
    public void WorkedExampleFromThePlan()
    {
        var proxies = new[] { Proxy(1, 3), Proxy(2, 3), Proxy(3, 3), Proxy(4, 3) };
        var numbers = new[]
        {
            Number(1, 1), Number(2, 1), Number(3, 1),
            Number(4, 2), Number(5, 2),
            Number(6, 3),
        };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Empty(plan.Unassigned);
        var loads = plan.Assignments.GroupBy(a => a.ProxyId).Select(g => g.Count()).OrderByDescending(c => c);
        Assert.Equal([2, 2, 1, 1], loads);

        // Ana (3 números) ficou em 3 proxies distintos.
        var anaProxies = plan.Assignments
            .Where(a => numbers.First(n => n.Id == a.NumberId).SellerId == Id(901))
            .Select(a => a.ProxyId).Distinct();
        Assert.Equal(3, anaProxies.Count());
    }

    // Capacidade estourada não é ignorada: o excedente fica SEM proxy e a tela
    // avisa. Empilhar em silêncio esconderia justamente o que ela existe para mostrar.
    [Fact]
    public void LeavesTheOverflowUnassigned()
    {
        var proxies = new[] { Proxy(1, capacity: 1), Proxy(2, capacity: 1) };
        var numbers = new[] { Number(1, 1), Number(2, 2), Number(3, 3) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Equal(2, plan.Assignments.Count);
        Assert.Single(plan.Unassigned);
    }

    // Mais números que proxies: distribui o mais uniformemente possível, sem
    // caso especial no código — o critério do vendedor nunca chega a zero e a
    // carga assume.
    [Fact]
    public void WithMoreNumbersThanProxies_DistributesEvenly()
    {
        var proxies = new[] { Proxy(1, 5), Proxy(2, 5) };
        var numbers = Enumerable.Range(1, 5).Select(i => Number(i, i)).ToArray();

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        var loads = plan.Assignments.GroupBy(a => a.ProxyId).Select(g => g.Count()).OrderBy(c => c);
        Assert.Equal([2, 3], loads);
    }

    // Proxy suspeito, pausado ou com teste falho não recebe número novo.
    [Fact]
    public void IgnoresProxiesThatDoNotAcceptNewNumbers()
    {
        var proxies = new[] { Proxy(1, accepts: false), Proxy(2) };
        var numbers = new[] { Number(1, 1), Number(2, 2) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.All(plan.Assignments, a => Assert.Equal(Id(2), a.ProxyId));
    }

    // Empatados em vendedor e carga, o proxy com menos ban recente ganha.
    [Fact]
    public void PrefersTheProxyWithFewerRecentBans()
    {
        var proxies = new[] { Proxy(1, bans: 3), Proxy(2, bans: 0) };
        var numbers = new[] { Number(1, 1) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Equal(Id(2), Assert.Single(plan.Assignments).ProxyId);
    }

    // Número que já tem proxy não se move: trocar de IP custa restart de socket
    // e é fator de risco. Rebalancear é decisão humana, com prévia.
    [Fact]
    public void DoesNotMoveNumbersThatAlreadyHaveAProxy()
    {
        var proxies = new[] { Proxy(1, capacity: 5), Proxy(2, capacity: 5) };
        var numbers = new[] { Number(1, 1, current: Id(1)), Number(2, 1, current: Id(1)) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Empty(plan.Assignments);
        Assert.Empty(plan.Unassigned);
    }

    // Número cujo proxy sumiu (revogado/vencido, fora dos candidatos) é
    // reatribuído mesmo sem AllowMoves: ficar num proxy morto não é opção.
    [Fact]
    public void ReassignsNumbersWhoseProxyIsGone()
    {
        var proxies = new[] { Proxy(2) };
        var numbers = new[] { Number(1, 1, current: Id(99)) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        var assignment = Assert.Single(plan.Assignments);
        Assert.Equal(Id(2), assignment.ProxyId);
        Assert.Equal(Id(99), assignment.FromProxyId);
    }

    // Com AllowMoves o rebalanceamento desconcentra o vendedor. A asserção é
    // sobre a colocação FINAL, não sobre quantos se moveram: quem já estava no
    // proxy certo fica onde está, e mover menos é a virtude, não o defeito.
    [Fact]
    public void WithAllowMoves_RebalancesConcentratedSeller()
    {
        var proxies = new[] { Proxy(1, 5), Proxy(2, 5) };
        var numbers = new[] { Number(1, 1, current: Id(1)), Number(2, 1, current: Id(1)) };

        var plan = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions(AllowMoves: true));

        var finalProxy = numbers.ToDictionary(
            n => n.Id,
            n => plan.Assignments.FirstOrDefault(a => a.NumberId == n.Id)?.ProxyId ?? n.CurrentProxyId);

        Assert.Equal(2, finalProxy.Values.Distinct().Count());
        Assert.Single(plan.Assignments);
    }

    // Mesmo estado de entrada, mesmo resultado: sem determinismo não há teste
    // confiável nem prévia que bata com o que será aplicado.
    [Fact]
    public void IsDeterministic()
    {
        var proxies = new[] { Proxy(1), Proxy(2), Proxy(3) };
        var numbers = new[] { Number(1, 1), Number(2, 2), Number(3, 1), Number(4, 3) };

        var first = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());
        var second = ProxyAllocator.Allocate(numbers, proxies, new AllocationOptions());

        Assert.Equal(
            first.Assignments.Select(a => (a.NumberId, a.ProxyId)),
            second.Assignments.Select(a => (a.NumberId, a.ProxyId)));
    }

    // Sem proxy nenhum disponível, todos ficam sem proxy — e nada estoura.
    [Fact]
    public void WithoutProxies_EverythingIsUnassigned()
    {
        var numbers = new[] { Number(1, 1), Number(2, 2) };

        var plan = ProxyAllocator.Allocate(numbers, [], new AllocationOptions());

        Assert.Empty(plan.Assignments);
        Assert.Equal(2, plan.Unassigned.Count);
    }
}
