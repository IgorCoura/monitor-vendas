using System.Globalization;

namespace MonitorVendas.Api.Features.Numbers.Health;

public enum HealthLevel
{
    // Sem tráfego não é "saudável" nem "doente": sem essa distinção, todo número
    // recém-conectado nasceria em alarme falso.
    NoData = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public sealed record HealthSignal(string Key, string Value, int Points);

public sealed record NumberHealthResult(int Score, HealthLevel Level, IReadOnlyList<HealthSignal> Signals);

public sealed record NumberHealthInput(
    int DeliveryConsidered,
    int DeliveryMissing,
    int InboundConversations,
    int InboundConversationsReplied,
    int OutboundConversations,
    int DisconnectionsLast24h,
    double NewContactsPerDay,
    bool SendRestricted,
    int BanEvents);

// Semáforo de saúde do número: agrega sinais que já estão no banco num score
// 0–100. Não existe "quality rating" fora da API oficial — este score é o
// substituto que se constrói. Devolve TAMBÉM os sinais que pesaram: a tela
// precisa dizer POR QUE o número está amarelo, não só que está.
public static class NumberHealth
{
    public static NumberHealthResult Evaluate(NumberHealthInput input)
    {
        if (input is { DeliveryConsidered: 0, InboundConversations: 0, OutboundConversations: 0, BanEvents: 0, SendRestricted: false })
            return new NumberHealthResult(0, HealthLevel.NoData, []);

        var signals = new List<HealthSignal>();

        // Entrega é o melhor early-warning que existe: número em soft-ban segue
        // "Ativo" e aceitando sendText — só que o ack 1 nunca vira ack 2. Amostra
        // mínima de 5 para uma mensagem perdida não gritar sozinha.
        if (input.DeliveryConsidered >= 5)
        {
            var rate = 1.0 - (double)input.DeliveryMissing / input.DeliveryConsidered;
            if (rate < 0.60)
                signals.Add(new("delivery", Percent(rate), 30));
            else if (rate < 0.85)
                signals.Add(new("delivery", Percent(rate), 15));
        }

        // Conversa recebida que ninguém responde: consenso de risco em <15%.
        if (input.InboundConversations >= 3)
        {
            var rate = (double)input.InboundConversationsReplied / input.InboundConversations;
            if (rate < 0.15)
                signals.Add(new("response", Percent(rate), 15));
            else if (rate < 0.30)
                signals.Add(new("response", Percent(rate), 8));
        }

        // Quem só dispara e pouco recebe tem o perfil do vetor nº 1 de ban
        // (mensagem para quem nunca te escreveu).
        var total = input.InboundConversations + input.OutboundConversations;
        if (total >= 3)
        {
            var share = (double)input.OutboundConversations / total;
            if (share > 0.50)
                signals.Add(new("outboundShare", Percent(share), 15));
            else if (share > 0.30)
                signals.Add(new("outboundShare", Percent(share), 8));
        }

        if (input.DisconnectionsLast24h >= 6)
            signals.Add(new("disconnections", Int(input.DisconnectionsLast24h), 30));
        else if (input.DisconnectionsLast24h >= 3)
            signals.Add(new("disconnections", Int(input.DisconnectionsLast24h), 15));

        // ≤20 novos contatos/dia para número novo, ≤50 para aquecido — é o limite
        // com mais lastro na evidência.
        if (input.NewContactsPerDay > 50)
            signals.Add(new("newContactsPerDay", Rate(input.NewContactsPerDay), 20));
        else if (input.NewContactsPerDay > 20)
            signals.Add(new("newContactsPerDay", Rate(input.NewContactsPerDay), 10));

        // O WhatsApp avisou (463): não é palpite, é a plataforma falando.
        if (input.SendRestricted)
            signals.Add(new("sendRestriction", "463", 25));

        if (input.BanEvents > 0)
            signals.Add(new("ban", Int(input.BanEvents), 40));

        var score = Math.Min(100, signals.Sum(s => s.Points));
        return new NumberHealthResult(score, LevelFor(score), signals);
    }

    private static HealthLevel LevelFor(int score) => score switch
    {
        >= 85 => HealthLevel.Critical,
        >= 60 => HealthLevel.High,
        >= 30 => HealthLevel.Medium,
        _ => HealthLevel.Low,
    };

    private static string Percent(double rate) =>
        ((int)Math.Round(rate * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Rate(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
