using MonitorVendas.Api.Common;

namespace MonitorVendas.Api.Features.Warmup;

public sealed record DailyTarget(int Goal, int EffectiveGoal, int Deficit, bool CappedByGraph);

// A aritmética do volume, isolada e pura para ser provada por teste sem banco.
//
// A lógica é de PISO, não de teto: a meta é quanta mensagem o número deveria ter
// no dia para parecer ativo, e o aquecimento completa só o que o tráfego real
// não cobriu. Número que conversou muito com aluno recebe pouco ou nada.
public static class WarmupPlan
{
    // Meta sorteada por número E por dia. Piso fixo para todos, todo dia, seria
    // regular demais — e regularidade é o que denuncia.
    public static DailyTarget TargetFor(
        Guid peerId,
        DateOnly day,
        int peerCount,
        int realMessagesToday,
        int warmupMessagesToday,
        WarmupOptions options)
    {
        var goal = SeededGoal(peerId, day, options);

        // Capacidade do grafo: com 4 números cada um tem 3 colegas, e sem este
        // limite seriam 10 mensagens/dia com o mesmo colega, todo dia. O teto
        // sobe sozinho conforme o pool cresce.
        var capacity = Math.Max(0, peerCount) * Math.Max(1, options.MaxMessagesPerPairPerDay);
        var effective = Math.Min(goal, capacity);

        var deficit = Math.Max(0, effective - realMessagesToday - warmupMessagesToday);
        return new DailyTarget(goal, effective, deficit, CappedByGraph: capacity < goal);
    }

    // Determinístico por (número, dia): duas passadas do agendador no mesmo dia
    // precisam concordar sobre a meta, senão o volume oscilaria a cada ciclo.
    private static int SeededGoal(Guid peerId, DateOnly day, WarmupOptions options)
    {
        var min = Math.Max(0, options.MinDailyMessages);
        var max = Math.Max(min, options.MaxDailyMessages);
        if (max == min)
            return min;

        var seed = HashCode.Combine(peerId, day.DayNumber);
        var slot = (uint)seed % (uint)(max - min + 1);
        return min + (int)slot;
    }

    // Quantos turnos a próxima conversa terá, respeitando o que ainda falta para
    // a meta — conversa que estoura o dia inteiro de uma vez é rajada.
    public static int TurnsFor(int deficit, WarmupOptions options, IRandomSource random)
    {
        var min = Math.Max(2, options.MinTurnsPerConversation);
        var max = Math.Max(min, options.MaxTurnsPerConversation);
        var drawn = min + (int)(random.NextDouble() * (max - min + 1));
        return Math.Clamp(Math.Min(drawn, deficit), 0, max);
    }

    // Intervalo entre dois turnos: log-normal, mediana perto do mínimo e cauda
    // longa. Gente responde em 40 segundos ou em duas horas — quase nunca
    // "sempre em 5 minutos".
    public static TimeSpan GapBetweenTurns(WarmupOptions options, IRandomSource random)
    {
        var min = Math.Max(5, options.MinTurnGapSeconds);
        var max = Math.Max(min + 1, options.MaxTurnGapSeconds);
        var u = random.NextDouble();
        return TimeSpan.FromSeconds(min + (max - min) * u * u * u);
    }

    // Fim de semana existe, mas bem reduzido; madrugada é zero. O expediente em
    // si vem do BusinessHoursCalendar, que já sabe de feriado.
    public static bool IsSendableMoment(DateTime localNow, bool businessHours, WarmupOptions options, IRandomSource random)
    {
        if (localNow.Hour < options.MorningFromHour || localNow.Hour >= options.EveningUntilHour)
            return false;

        var weekend = localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (weekend)
            return random.NextDouble() < Math.Clamp(options.WeekendFactor, 0, 1);

        return businessHours || random.NextDouble() < Math.Clamp(options.OffHoursChance, 0, 1);
    }
}
