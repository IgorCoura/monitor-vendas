using MonitorVendas.Api.Common;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Common;

public class HumanDelayTests
{
    // Sem sorteio de pausa (uniform 0.5 > 8%): o delay é texto × 30ms × fator.
    private static readonly FixedRandomSource NoPause = new(gaussian: 1.0, uniform: 0.5);

    // Texto curto não sai instantâneo: "ok" digitado em 60ms seria assinatura de robô.
    [Fact]
    public void ForText_ShortText_UsesFloor()
    {
        Assert.Equal(HumanDelay.MinMs, HumanDelay.ForText(2, NoPause));
    }

    // Texto enorme não passa do teto: 105s de "digitando" é tão inumano quanto zero.
    [Fact]
    public void ForText_HugeText_UsesCeiling()
    {
        Assert.Equal(HumanDelay.MaxMs, HumanDelay.ForText(3500, NoPause));
    }

    // Entre o piso e o teto, o delay cresce com o tamanho do texto (30ms/char).
    [Fact]
    public void ForText_GrowsWithTextLength()
    {
        var shorter = HumanDelay.ForText(100, NoPause);
        var longer = HumanDelay.ForText(200, NoPause);

        Assert.Equal(3000, shorter);
        Assert.Equal(6000, longer);
    }

    // O fator gaussiano acelera ou retarda a digitação — dois envios do mesmo texto
    // não saem sempre com o mesmo delay.
    [Fact]
    public void ForText_AppliesSpeedFactor()
    {
        var fast = HumanDelay.ForText(100, new FixedRandomSource(gaussian: 0.5, uniform: 0.5));
        var slow = HumanDelay.ForText(100, new FixedRandomSource(gaussian: 2.0, uniform: 0.5));

        Assert.Equal(1500, fast);
        Assert.Equal(6000, slow);
    }

    // Fator sorteado absurdo (cauda da gaussiana) é truncado: velocidade negativa
    // ou 10x não é humano.
    [Fact]
    public void ForText_ClampsInsaneFactors()
    {
        var negative = HumanDelay.ForText(100, new FixedRandomSource(gaussian: -3.0, uniform: 0.5));
        var extreme = HumanDelay.ForText(100, new FixedRandomSource(gaussian: 10.0, uniform: 0.5));

        Assert.Equal(HumanDelay.MinMs, negative);   // truncado em 0.4 → 1200 pelo piso
        Assert.Equal(6000, extreme);                // truncado em 2.0
    }

    // Sorteio abaixo de 8% adiciona a "pausa para pensar" ao delay base.
    [Fact]
    public void ForText_SometimesAddsThinkingPause()
    {
        var withPause = HumanDelay.ForText(100, new FixedRandomSource(gaussian: 1.0, uniform: 0.05));

        // base 3000 + pausa (800 + 0.05 × 2700) = 3935
        Assert.Equal(3935, withPause);
    }
}
