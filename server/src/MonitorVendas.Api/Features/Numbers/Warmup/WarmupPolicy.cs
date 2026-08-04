namespace MonitorVendas.Api.Features.Numbers.Warmup;

public sealed record WarmupStep(int ThroughDay, int MessagesPerDay, int NewContactsPerDay);

public enum WarmupState
{
    // Nunca conectou, ou é anterior a esta feature: sem curva para aplicar.
    NoData = 0,
    Warming = 1,
    Paused = 2,
    Mature = 3,
}

public sealed record WarmupLimits(int Day, int? MessagesPerDay, int NewContactsPerDay, WarmupState State)
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
    public static WarmupLimits LimitsFor(
        DateTime? warmupStartedAt,
        DateTime now,
        WarmupOptions options,
        DateTime? pausedAt = null,
        DateTime? completedAt = null)
    {
        // Número sem data de aquecimento é número que nunca conectou: sem
        // histórico, trata-se como maduro para não travar quem já operava antes
        // desta feature existir.
        if (!options.Enabled || warmupStartedAt is not { } start)
            return new WarmupLimits(0, null, options.MatureNewContactsPerDay, WarmupState.NoData);

        // Liberado à mão por quem opera: sai da curva sem esperar os 30 dias.
        if (completedAt is not null)
            return new WarmupLimits(DayOf(start, now), null, options.MatureNewContactsPerDay, WarmupState.Mature);

        // Pausado: o relógio para no instante da pausa em vez de continuar
        // correndo. Manter a função pura — ela só recebe datas — é o que
        // permite testar a pausa sem banco.
        var reference = pausedAt ?? now;
        var day = DayOf(start, reference);
        var state = pausedAt is null ? WarmupState.Warming : WarmupState.Paused;

        foreach (var step in options.Curve.OrderBy(s => s.ThroughDay))
            if (day <= step.ThroughDay)
                return new WarmupLimits(day, step.MessagesPerDay, step.NewContactsPerDay, state);

        return new WarmupLimits(day, null, options.MatureNewContactsPerDay, WarmupState.Mature);
    }

    // Total de dias da curva, para a tela desenhar "dia 5 de 30".
    public static int TotalDays(WarmupOptions options) =>
        options.Curve.Count == 0 ? 0 : options.Curve.Max(s => s.ThroughDay);

    // Dia 1 é o próprio dia da primeira conexão.
    private static int DayOf(DateTime start, DateTime reference) =>
        (int)(reference.Date - start.Date).TotalDays + 1;
}
