namespace MonitorVendas.Api.Features.Numbers.Warmup;

public sealed record WarmupStep(int ThroughDay, int MessagesPerDay, int NewContactsPerDay);

public sealed record WarmupLimits(int Day, int? MessagesPerDay, int NewContactsPerDay)
{
    public bool InWarmup => MessagesPerDay is not null;
}

public sealed class WarmupOptions
{
    public const string Section = "Warmup";

    public bool Enabled { get; set; } = true;

    // A curva é config, não código: ajustá-la não pode exigir recompilar. A
    // mediana das fontes consultadas; ver docs/plano-antiban-sugestoes.md §B1.
    public List<WarmupStep> Curve { get; set; } =
    [
        new(ThroughDay: 3, MessagesPerDay: 20, NewContactsPerDay: 0),
        new(ThroughDay: 7, MessagesPerDay: 50, NewContactsPerDay: 2),
        new(ThroughDay: 14, MessagesPerDay: 120, NewContactsPerDay: 2),
        new(ThroughDay: 21, MessagesPerDay: 250, NewContactsPerDay: 10),
        new(ThroughDay: 30, MessagesPerDay: 300, NewContactsPerDay: 20),
    ];

    // Depois da curva vale a cota normal — o número está aquecido.
    public int MatureNewContactsPerDay { get; set; } = 50;
}

// Teto progressivo de envio para número novo. Este componente NÃO envia nada:
// ele limita o que o vendedor já faria. Tráfego sintético (números do sistema
// conversando entre si) foi descartado de propósito — é um grafo fechado com
// reciprocidade perfeita e horários correlacionados, exatamente o padrão que
// detector de comportamento coordenado procura, e ainda contaminaria as
// próprias métricas do produto.
public static class WarmupPolicy
{
    public static WarmupLimits LimitsFor(DateTime? warmupStartedAt, DateTime now, WarmupOptions options)
    {
        // Número sem data de aquecimento é número que nunca conectou: sem
        // histórico, trata-se como maduro para não travar quem já operava antes
        // desta feature existir.
        if (!options.Enabled || warmupStartedAt is not { } start)
            return new WarmupLimits(0, null, options.MatureNewContactsPerDay);

        // Dia 1 é o próprio dia da primeira conexão.
        var day = (int)(now.Date - start.Date).TotalDays + 1;

        foreach (var step in options.Curve.OrderBy(s => s.ThroughDay))
            if (day <= step.ThroughDay)
                return new WarmupLimits(day, step.MessagesPerDay, step.NewContactsPerDay);

        return new WarmupLimits(day, null, options.MatureNewContactsPerDay);
    }
}
