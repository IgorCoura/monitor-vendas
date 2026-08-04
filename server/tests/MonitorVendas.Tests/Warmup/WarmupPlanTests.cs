using MonitorVendas.Api.Features.Warmup;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Warmup;

// A aritmética do volume é de PISO, não de teto: a meta é quanta mensagem o
// número deveria ter no dia, e o aquecimento completa só o que o tráfego real
// não cobriu.
public class WarmupPlanTests
{
    private static WarmupOptions Options() => new()
    {
        MinDailyMessages = 20,
        MaxDailyMessages = 40,
        MaxMessagesPerPairPerDay = 6,
    };

    private static readonly DateOnly Day = new(2026, 8, 4);

    // A meta do dia cai dentro da faixa configurada.
    [Fact]
    public void Goal_StaysInsideTheConfiguredRange()
    {
        var options = Options();

        for (var i = 0; i < 200; i++)
        {
            var target = WarmupPlan.TargetFor(Guid.NewGuid(), Day, 10, 0, 0, options);
            Assert.InRange(target.Goal, 20, 40);
        }
    }

    // Mesma dupla (número, dia) dá sempre a mesma meta: duas passadas do
    // agendador no mesmo dia precisam concordar, senão o volume oscilaria a
    // cada ciclo.
    [Fact]
    public void Goal_IsStableForTheSamePeerAndDay()
    {
        var peer = Guid.NewGuid();
        var options = Options();

        Assert.Equal(
            WarmupPlan.TargetFor(peer, Day, 10, 0, 0, options).Goal,
            WarmupPlan.TargetFor(peer, Day, 10, 0, 0, options).Goal);
    }

    // A meta muda de um dia para o outro: piso fixo todo dia seria regular
    // demais, e regularidade é o que denuncia.
    [Fact]
    public void Goal_VariesAcrossDays()
    {
        var peer = Guid.NewGuid();
        var options = Options();

        var goals = Enumerable.Range(0, 30)
            .Select(d => WarmupPlan.TargetFor(peer, Day.AddDays(d), 10, 0, 0, options).Goal)
            .Distinct()
            .Count();

        Assert.True(goals > 1, "a meta diária não pode ser constante");
    }

    // Mensagem real com aluno abate a meta: número que trabalhou muito recebe
    // pouco ou nada de aquecimento.
    [Fact]
    public void RealTraffic_ReducesTheDeficit()
    {
        var peer = Guid.NewGuid();
        var options = Options();

        var idle = WarmupPlan.TargetFor(peer, Day, 10, 0, 0, options);
        var busy = WarmupPlan.TargetFor(peer, Day, 10, realMessagesToday: 15, 0, options);

        Assert.Equal(idle.Deficit - 15, busy.Deficit);
    }

    // Tráfego real acima da meta zera o aquecimento — nunca vira número negativo
    // nem "crédito" para o dia seguinte.
    [Fact]
    public void RealTrafficAboveTheGoal_LeavesNothingToDo()
    {
        var target = WarmupPlan.TargetFor(Guid.NewGuid(), Day, 10, realMessagesToday: 500, 0, Options());

        Assert.Equal(0, target.Deficit);
    }

    // O que o próprio aquecimento já mandou hoje também abate: sem isso ele
    // perseguiria a meta para sempre.
    [Fact]
    public void WarmupAlreadySentToday_ReducesTheDeficit()
    {
        var peer = Guid.NewGuid();
        var options = Options();

        var before = WarmupPlan.TargetFor(peer, Day, 10, 0, 0, options);
        var after = WarmupPlan.TargetFor(peer, Day, 10, 0, warmupMessagesToday: 5, options);

        Assert.Equal(before.Deficit - 5, after.Deficit);
    }

    // Pool pequeno capa a meta pela capacidade do grafo: com 3 colegas e teto de
    // 6 por par, 18 é o máximo — sem isso seriam 10 mensagens/dia com o mesmo
    // colega, todo dia, que é exatamente o padrão que se quer evitar.
    [Fact]
    public void SmallPool_CapsTheGoalByGraphCapacity()
    {
        var target = WarmupPlan.TargetFor(Guid.NewGuid(), Day, peerCount: 3, 0, 0, Options());

        Assert.Equal(18, target.EffectiveGoal);
        Assert.True(target.CappedByGraph);
        Assert.True(target.Goal > target.EffectiveGoal);
    }

    // Pool grande não é capado, e a tela não mostra aviso de teto.
    [Fact]
    public void LargePool_IsNotCapped()
    {
        var target = WarmupPlan.TargetFor(Guid.NewGuid(), Day, peerCount: 20, 0, 0, Options());

        Assert.False(target.CappedByGraph);
        Assert.Equal(target.Goal, target.EffectiveGoal);
    }

    // Número sem nenhum colega no grafo não tem para quem mandar: meta efetiva
    // zero, e não uma dívida que se acumula.
    [Fact]
    public void PeerWithoutLinks_HasNothingToSend()
    {
        var target = WarmupPlan.TargetFor(Guid.NewGuid(), Day, peerCount: 0, 0, 0, Options());

        Assert.Equal(0, target.EffectiveGoal);
        Assert.Equal(0, target.Deficit);
    }

    // A conversa nunca estoura o que falta para a meta: despejar o dia inteiro
    // de uma vez é rajada.
    [Fact]
    public void TurnsFor_NeverExceedsTheDeficit()
    {
        var options = Options();

        Assert.True(WarmupPlan.TurnsFor(2, options, new FixedRandomSource(uniform: 0.99)) <= 2);
    }

    // ...nem passa do máximo por conversa, mesmo com déficit enorme.
    [Fact]
    public void TurnsFor_NeverExceedsTheMaximum()
    {
        var options = Options();

        for (var u = 0.0; u < 1; u += 0.05)
            Assert.InRange(WarmupPlan.TurnsFor(100, options, new FixedRandomSource(uniform: u)), 0, options.MaxTurnsPerConversation);
    }

    // O intervalo entre turnos tem mediana perto do mínimo e cauda longa: gente
    // responde em 40 segundos ou em duas horas, quase nunca "sempre em 5 min".
    [Fact]
    public void GapBetweenTurns_IsSkewedTowardTheMinimum()
    {
        var options = Options();
        var median = WarmupPlan.GapBetweenTurns(options, new FixedRandomSource(uniform: 0.5));
        var tail = WarmupPlan.GapBetweenTurns(options, new FixedRandomSource(uniform: 0.99));
        var range = options.MaxTurnGapSeconds - options.MinTurnGapSeconds;

        Assert.True(median.TotalSeconds < options.MinTurnGapSeconds + range * 0.2);
        Assert.True(tail.TotalSeconds > options.MinTurnGapSeconds + range * 0.9);
    }

    // Madrugada é zero, independentemente de sorteio.
    [Fact]
    public void DeadOfNight_IsNeverSendable()
    {
        var options = Options();
        var random = new FixedRandomSource(uniform: 0.0);

        Assert.False(WarmupPlan.IsSendableMoment(new DateTime(2026, 8, 4, 3, 0, 0), true, options, random));
        Assert.False(WarmupPlan.IsSendableMoment(new DateTime(2026, 8, 4, 23, 0, 0), true, options, random));
    }

    // Dentro do expediente em dia útil não depende de sorte nenhuma.
    [Fact]
    public void DuringBusinessHours_IsAlwaysSendable()
    {
        // Terça-feira, 14h.
        Assert.True(WarmupPlan.IsSendableMoment(
            new DateTime(2026, 8, 4, 14, 0, 0), businessHours: true, Options(), new FixedRandomSource(uniform: 0.99)));
    }

    // Fora do expediente mas ainda de noite: passa às vezes, é a cauda de quem
    // manda mensagem depois do trabalho.
    [Fact]
    public void AfterHours_IsSendableOnlySometimes()
    {
        var options = Options();
        var evening = new DateTime(2026, 8, 4, 20, 0, 0);

        Assert.True(WarmupPlan.IsSendableMoment(evening, false, options, new FixedRandomSource(uniform: 0.1)));
        Assert.False(WarmupPlan.IsSendableMoment(evening, false, options, new FixedRandomSource(uniform: 0.9)));
    }

    // Fim de semana existe, mas bem reduzido — o sábado do WeekendFactor.
    [Fact]
    public void Weekend_IsSendableRarely()
    {
        var options = Options();
        // Sábado, 15h.
        var saturday = new DateTime(2026, 8, 8, 15, 0, 0);

        Assert.True(WarmupPlan.IsSendableMoment(saturday, true, options, new FixedRandomSource(uniform: 0.1)));
        Assert.False(WarmupPlan.IsSendableMoment(saturday, true, options, new FixedRandomSource(uniform: 0.5)));
    }
}
