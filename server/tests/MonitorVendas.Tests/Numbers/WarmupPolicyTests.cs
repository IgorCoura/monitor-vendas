using MonitorVendas.Api.Features.Numbers.Warmup;

namespace MonitorVendas.Tests.Numbers;

public class WarmupPolicyTests
{
    private static readonly DateTime Start = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly WarmupOptions Options = new();

    // Dia 1 é o próprio dia da primeira conexão: 20 mensagens e nenhum contato novo.
    [Fact]
    public void FirstDay_HasTheTightestCeiling()
    {
        var limits = WarmupPolicy.LimitsFor(Start, Start, Options);

        Assert.Equal(1, limits.Day);
        Assert.Equal(20, limits.MessagesPerDay);
        Assert.Equal(0, limits.NewContactsPerDay);
        Assert.True(limits.InWarmup);
    }

    // A curva sobe por faixa: dia 5 já permite 50, dia 10 permite 120.
    [Theory]
    [InlineData(5, 50)]
    [InlineData(10, 120)]
    [InlineData(20, 250)]
    [InlineData(28, 300)]
    public void RampsUpAlongTheCurve(int day, int expected)
    {
        var limits = WarmupPolicy.LimitsFor(Start, Start.AddDays(day - 1), Options);

        Assert.Equal(day, limits.Day);
        Assert.Equal(expected, limits.MessagesPerDay);
    }

    // Passada a curva, o número está aquecido: sem teto de aquecimento (vale só
    // a cota normal) e o limite maduro de contatos novos.
    [Fact]
    public void AfterTheCurve_TheNumberIsMature()
    {
        var limits = WarmupPolicy.LimitsFor(Start, Start.AddDays(45), Options);

        Assert.False(limits.InWarmup);
        Assert.Null(limits.MessagesPerDay);
        Assert.Equal(Options.MatureNewContactsPerDay, limits.NewContactsPerDay);
    }

    // Número sem data de aquecimento (nunca conectou, ou é anterior a esta
    // feature) é tratado como maduro: travar quem já operava seria punir o
    // histórico existente.
    [Fact]
    public void WithoutAStartDate_IsTreatedAsMature()
    {
        var limits = WarmupPolicy.LimitsFor(null, Start, Options);

        Assert.False(limits.InWarmup);
    }

    // Desligado por config, nada é limitado.
    [Fact]
    public void WhenDisabled_ImposesNoCeiling()
    {
        var limits = WarmupPolicy.LimitsFor(Start, Start, new WarmupOptions { Enabled = false });

        Assert.False(limits.InWarmup);
    }

    // A curva vem da config: mudar as faixas não exige recompilar.
    [Fact]
    public void UsesTheConfiguredCurve()
    {
        var options = new WarmupOptions { Curve = [new WarmupStep(ThroughDay: 2, MessagesPerDay: 5, NewContactsPerDay: 0)] };

        Assert.Equal(5, WarmupPolicy.LimitsFor(Start, Start.AddDays(1), options).MessagesPerDay);
        Assert.False(WarmupPolicy.LimitsFor(Start, Start.AddDays(2), options).InWarmup);
    }
}

public class OptOutDetectorTests
{
    // Pedidos inequívocos de descadastro, com e sem acento/pontuação.
    [Theory]
    [InlineData("SAIR")]
    [InlineData("sair")]
    [InlineData("Pare!")]
    [InlineData("parar")]
    [InlineData("Não quero mais receber")]
    [InlineData("me remova")]
    [InlineData("STOP")]
    public void RecognizesOptOutRequests(string text)
    {
        Assert.True(MonitorVendas.Api.Features.Conversations.OptOutDetector.IsOptOut(text));
    }

    // Conversa normal de venda NÃO vira opt-out: um falso positivo aqui silencia
    // um cliente ativo, que é erro pior que deixar passar.
    [Theory]
    [InlineData("não quero o azul, quero o vermelho")]
    [InlineData("para quando fica pronto?")]
    [InlineData("vou parar na loja amanhã pra buscar, pode separar?")]
    [InlineData("bom dia, tudo bem?")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotFlagNormalConversation(string? text)
    {
        Assert.False(MonitorVendas.Api.Features.Conversations.OptOutDetector.IsOptOut(text));
    }

    // Num texto longo só a frase completa conta: "pare" solto no meio de um
    // parágrafo quase sempre é outra coisa.
    [Fact]
    public void InLongText_OnlyFullPhrasesCount()
    {
        const string longText = "Oi, tudo bem? Recebi o produto e gostei muito, mas queria saber se pare"
            + " de fabricar esse modelo vocês avisam os clientes antigos?";

        Assert.False(MonitorVendas.Api.Features.Conversations.OptOutDetector.IsOptOut(longText));
        Assert.True(MonitorVendas.Api.Features.Conversations.OptOutDetector.IsOptOut(
            "Boa tarde, por gentileza nao quero mais receber essas mensagens de vocês, obrigado."));
    }
}
