using MonitorVendas.Api.Features.Warmup;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Warmup;

public class WarmupGraphTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WarmupOptions Options = new();
    private static readonly FixedRandomSource Random = new();

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    // `weeksAgo` controla há quanto tempo o número está no pool: é isso que
    // decide o tamanho do círculo dele.
    private static GraphPeer Peer(int n, double weeksAgo = 0) =>
        new(Id(n), Now.AddDays(-weeksAgo * 7));

    private static IReadOnlyList<NewLink> Grow(IReadOnlyList<GraphPeer> peers, params GraphLink[] existing) =>
        WarmupGraph.Grow(peers, existing, Now, Options, Random);

    // Número que acabou de entrar ganha UM colega, não o círculo inteiro:
    // ninguém conhece quatro colegas no primeiro dia.
    [Fact]
    public void NewPeer_GetsASingleCoreLink()
    {
        var links = Grow([Peer(1), Peer(2), Peer(3), Peer(4)]);

        Assert.All(new[] { Id(1), Id(2), Id(3), Id(4) },
            id => Assert.Equal(1, links.Count(l => l.PeerAId == id || l.PeerBId == id)));
    }

    // Uma semana depois o círculo tem dois; o crescimento é de um por semana.
    [Fact]
    public void CircleGrowsOnePeerPerWeek()
    {
        var links = Grow([Peer(1, 1), Peer(2, 1), Peer(3, 1), Peer(4, 1)]);

        Assert.Equal(2, links.Count(l => l.PeerAId == Id(1) || l.PeerBId == Id(1)));
    }

    // O círculo para no tamanho configurado, por mais antigo que o número seja.
    [Fact]
    public void CoreCircleStopsAtTheConfiguredSize()
    {
        var peers = Enumerable.Range(1, 10).Select(i => Peer(i, weeksAgo: 52)).ToList();

        var links = Grow(peers);

        var core = links.Where(l => l.Kind == WarmupLinkKind.Core).ToList();
        Assert.All(peers, p => Assert.True(
            core.Count(l => l.PeerAId == p.Id || l.PeerBId == p.Id) <= Options.CoreCircleSize));
    }

    // Arestas que já existem NUNCA são remontadas: relação real é estável, e
    // re-sortear o grafo toda semana é uma assinatura por si só.
    [Fact]
    public void ExistingLinksAreNeverRebuilt()
    {
        var existing = WarmupGraph.Normalize(Id(1), Id(2), WarmupLinkKind.Core);

        var links = Grow([Peer(1), Peer(2), Peer(3), Peer(4)], existing);

        Assert.DoesNotContain(links, l => l.PeerAId == existing.PeerAId && l.PeerBId == existing.PeerBId);
    }

    // Entrar um número novo não mexe em quem já estava: só cria arestas.
    [Fact]
    public void AddingAPeer_DoesNotTouchTheOthers()
    {
        var veterans = new[] { Peer(1, 4), Peer(2, 4), Peer(3, 4) };
        var before = Grow(veterans).Select(l => (l.PeerAId, l.PeerBId)).ToHashSet();
        var existing = before.Select(p => WarmupGraph.Normalize(p.PeerAId, p.PeerBId, WarmupLinkKind.Core)).ToArray();

        var after = Grow([.. veterans, Peer(4)], existing);

        // Tudo que saiu agora é aresta NOVA — nenhuma das antigas foi refeita.
        Assert.All(after, l => Assert.DoesNotContain((l.PeerAId, l.PeerBId), before));
    }

    // O par é normalizado (menor Guid primeiro): sem isso a mesma relação
    // entraria duas vezes com os lados trocados e o grau ficaria errado.
    [Fact]
    public void PairsAreNormalized()
    {
        var links = Grow([Peer(9), Peer(2), Peer(5)]);

        Assert.All(links, l => Assert.True(l.PeerAId.CompareTo(l.PeerBId) < 0));
    }

    // Com pool pequeno o núcleo é a equipe inteira e não há mais com quem
    // parear — a montagem para em vez de estourar.
    [Fact]
    public void WithATinyPool_StopsWhenThereIsNobodyLeft()
    {
        var links = Grow([Peer(1, 52), Peer(2, 52)]);

        Assert.Single(links);
    }

    // Sozinho não há par possível.
    [Fact]
    public void ASinglePeer_ProducesNothing()
    {
        Assert.Empty(Grow([Peer(1, 52)]));
    }

    // Ocasionais só aparecem depois de o núcleo estar completo, e mais devagar.
    [Fact]
    public void OccasionalLinksComeAfterTheCoreIsFull()
    {
        var peers = Enumerable.Range(1, 12).Select(i => Peer(i, weeksAgo: 52)).ToList();

        var links = Grow(peers);

        Assert.Contains(links, l => l.Kind == WarmupLinkKind.Occasional);
        // Núcleo primeiro: ninguém tem ocasional sem ter o núcleo cheio.
        foreach (var peer in peers)
        {
            var core = links.Count(l => l.Kind == WarmupLinkKind.Core && (l.PeerAId == peer.Id || l.PeerBId == peer.Id));
            var occasional = links.Count(l => l.Kind == WarmupLinkKind.Occasional && (l.PeerAId == peer.Id || l.PeerBId == peer.Id));
            if (occasional > 0)
                Assert.Equal(Options.CoreCircleSize, core);
        }
    }

    // A intensidade é sorteada por par dentro da faixa da camada: par uniforme
    // é o que denuncia grafo desenhado.
    [Fact]
    public void IntensityStaysWithinTheLayerRange()
    {
        var links = Grow([Peer(1), Peer(2), Peer(3), Peer(4)]);

        Assert.All(links.Where(l => l.Kind == WarmupLinkKind.Core), l =>
            Assert.InRange(l.ConversationsPerWeek,
                Options.CoreConversationsPerWeekMin, Options.CoreConversationsPerWeekMax));
    }

    // Mesmo estado de entrada, mesmo grafo: sem determinismo não há teste
    // confiável nem prévia que bata com o que será aplicado.
    [Fact]
    public void IsDeterministic()
    {
        var peers = new[] { Peer(1, 3), Peer(2, 3), Peer(3, 3), Peer(4, 1), Peer(5) };

        var first = Grow(peers).Select(l => (l.PeerAId, l.PeerBId, l.Kind)).ToList();
        var second = Grow(peers).Select(l => (l.PeerAId, l.PeerBId, l.Kind)).ToList();

        Assert.Equal(first, second);
    }
}
