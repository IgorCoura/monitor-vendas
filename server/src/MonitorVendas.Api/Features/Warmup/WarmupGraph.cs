using MonitorVendas.Api.Common;

namespace MonitorVendas.Api.Features.Warmup;

public sealed record GraphPeer(Guid Id, DateTime JoinedAt);

public sealed record GraphLink(Guid PeerAId, Guid PeerBId, WarmupLinkKind Kind);

public sealed record NewLink(Guid PeerAId, Guid PeerBId, WarmupLinkKind Kind, double ConversationsPerWeek);

// Monta o círculo de cada número. Puro de propósito, no espírito do
// MetricsCalculator e do ProxyAllocator: entra lista, sai lista, e cada regra é
// provada por teste rápido sem banco.
//
// Duas regras dão o formato: o círculo CRESCE com o tempo (um colega novo por
// semana — ninguém conhece quatro colegas no primeiro dia) e as arestas que já
// existem NUNCA são remontadas. Relação real é estável; re-sortear o grafo toda
// semana é uma assinatura por si só.
public static class WarmupGraph
{
    public static IReadOnlyList<NewLink> Grow(
        IReadOnlyList<GraphPeer> peers,
        IReadOnlyList<GraphLink> existing,
        DateTime now,
        WarmupOptions options,
        IRandomSource random)
    {
        if (peers.Count < 2)
            return [];

        var created = new List<NewLink>();

        // Trabalha sobre uma cópia mutável: uma aresta criada nesta passada já
        // conta para o grau da próxima decisão, senão o mesmo número receberia
        // vários colegas de uma vez.
        var links = existing.Select(l => Normalize(l.PeerAId, l.PeerBId, l.Kind)).ToList();

        // Quem está há mais tempo decide primeiro: assim o veterano completa o
        // círculo dele antes de o recém-chegado disputar as mesmas vagas.
        foreach (var peer in peers.OrderBy(p => p.JoinedAt).ThenBy(p => p.Id))
        {
            AddLayer(peer, peers, links, created, WarmupLinkKind.Core,
                TargetDegree(peer, now, options.CoreCircleSize, weeksPerPeer: 1),
                options.CoreConversationsPerWeekMin, options.CoreConversationsPerWeekMax,
                options, random);

            // Ocasionais só começam depois de o núcleo estar CHEIO — no tamanho
            // final, não na meta da semana. Comparar com a meta corrente daria
            // um ocasional já no primeiro dia, quando o número deveria ter
            // exatamente um colega.
            if (DegreeOf(links, peer.Id, WarmupLinkKind.Core) >= options.CoreCircleSize)
            {
                // O relógio dos ocasionais começa quando o núcleo fechou, e anda
                // na metade da velocidade.
                var weeksAfterCore = (now - peer.JoinedAt).TotalDays / 7.0 - options.CoreCircleSize;
                var target = Math.Min(options.OccasionalCircleSize, 1 + (int)Math.Max(0, weeksAfterCore / 2));

                AddLayer(peer, peers, links, created, WarmupLinkKind.Occasional, target,
                    options.OccasionalConversationsPerWeekMin, options.OccasionalConversationsPerWeekMax,
                    options, random);
            }
        }

        return created;
    }

    // Um peer novo por período desde a entrada, até o tamanho final do círculo.
    private static int TargetDegree(GraphPeer peer, DateTime now, int max, int weeksPerPeer)
    {
        var weeks = Math.Max(0, (now - peer.JoinedAt).TotalDays / 7.0);
        return Math.Min(max, 1 + (int)(weeks / weeksPerPeer));
    }

    private static void AddLayer(
        GraphPeer peer,
        IReadOnlyList<GraphPeer> peers,
        List<GraphLink> links,
        List<NewLink> created,
        WarmupLinkKind kind,
        int target,
        double perWeekMin,
        double perWeekMax,
        WarmupOptions options,
        IRandomSource random)
    {
        while (DegreeOf(links, peer.Id, kind) < target)
        {
            // Candidato = quem ainda não tem aresta nenhuma com este número.
            // Empata pelo MENOR grau total: assim o grafo cresce equilibrado e
            // ninguém vira o centro de uma estrela.
            var candidate = peers
                .Where(p => p.Id != peer.Id)
                .Where(p => !links.Any(l => Touches(l, peer.Id, p.Id)))
                .OrderBy(p => DegreeOf(links, p.Id, kind))
                .ThenBy(p => TotalDegree(links, p.Id))
                .ThenBy(p => p.Id)
                .FirstOrDefault();

            // Pool pequeno: não há mais com quem fazer par. Com 4 números o
            // núcleo é a equipe inteira, e isso é aceitável.
            if (candidate is null)
                return;

            var perWeek = perWeekMin + random.NextDouble() * (perWeekMax - perWeekMin);
            var link = Normalize(peer.Id, candidate.Id, kind);

            links.Add(link);
            created.Add(new NewLink(link.PeerAId, link.PeerBId, kind, Math.Round(perWeek, 2)));
        }
    }

    // `PeerAId` é sempre o menor Guid: sem normalizar, o mesmo par entraria duas
    // vezes com os lados trocados e o grau ficaria errado.
    public static GraphLink Normalize(Guid a, Guid b, WarmupLinkKind kind) =>
        a.CompareTo(b) <= 0 ? new GraphLink(a, b, kind) : new GraphLink(b, a, kind);

    private static bool Touches(GraphLink link, Guid a, Guid b) =>
        (link.PeerAId == a && link.PeerBId == b) || (link.PeerAId == b && link.PeerBId == a);

    private static int DegreeOf(List<GraphLink> links, Guid peerId, WarmupLinkKind kind) =>
        links.Count(l => l.Kind == kind && (l.PeerAId == peerId || l.PeerBId == peerId));

    private static int TotalDegree(List<GraphLink> links, Guid peerId) =>
        links.Count(l => l.PeerAId == peerId || l.PeerBId == peerId);
}
