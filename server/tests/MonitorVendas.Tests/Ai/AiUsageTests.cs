using MonitorVendas.Api.Features.Ai;

namespace MonitorVendas.Tests.Ai;

// O saldo da janela é a soma do `CommittedBrl` dos registros. Cada estado pesa
// diferente: contar um liberado, ou um liquidado pela estimativa, faria o freio
// travar cedo demais ou tarde demais.
public class AiUsageTests
{
    private static AiUsage Usage(AiUsageStatus status, decimal estimated, decimal? actual = null) =>
        new() { Status = status, EstimatedBrl = estimated, ActualBrl = actual };

    // Reservado ainda não tem custo real: vale a estimativa, que é o que segura o
    // saldo enquanto a chamada está no ar.
    [Fact]
    public void Reserved_CommitsTheEstimate()
    {
        Assert.Equal(0.40m, Usage(AiUsageStatus.Reserved, 0.40m).CommittedBrl);
    }

    // Liquidado vale o custo real (com margem). Sem `ActualBrl` — que não deveria
    // acontecer — compromete zero em vez de estourar a soma do saldo.
    [Fact]
    public void Settled_CommitsTheRealCost()
    {
        Assert.Equal(0.11m, Usage(AiUsageStatus.Settled, 0.40m, 0.11m).CommittedBrl);
        Assert.Equal(0m, Usage(AiUsageStatus.Settled, 0.40m).CommittedBrl);
    }

    // Liberado não consome nada: a chamada não gerou cobrança do outro lado.
    [Fact]
    public void Released_CommitsNothing()
    {
        Assert.Equal(0m, Usage(AiUsageStatus.Released, 0.40m, 0.11m).CommittedBrl);
    }
}
