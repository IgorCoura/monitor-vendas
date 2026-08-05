using MonitorVendas.Api.Features.Numbers.Health;

namespace MonitorVendas.Tests.Numbers;

public class NumberHealthTests
{
    private static NumberHealthInput Baseline() => new(
        DeliveryConsidered: 0,
        DeliveryMissing: 0,
        InboundConversations: 0,
        InboundConversationsReplied: 0,
        OutboundConversations: 0,
        DisconnectionsLast24h: 0,
        NewContactsPerDay: 0,
        SendRestricted: false,
        BanEvents: 0);

    // Número sem tráfego nenhum é "sem dados", nunca "vermelho": alarme falso em
    // número recém-conectado ensinaria o operador a ignorar o semáforo.
    [Fact]
    public void WithoutAnyTraffic_IsNoData()
    {
        var result = NumberHealth.Evaluate(Baseline());

        Assert.Equal(HealthLevel.NoData, result.Level);
        Assert.Empty(result.Signals);
    }

    // Tráfego saudável (tudo entregue, respostas em dia) fica no nível baixo, sem sinais.
    [Fact]
    public void HealthyTraffic_IsLowWithoutSignals()
    {
        var result = NumberHealth.Evaluate(Baseline() with
        {
            DeliveryConsidered = 40,
            DeliveryMissing = 2,
            InboundConversations = 10,
            InboundConversationsReplied = 8,
        });

        Assert.Equal(HealthLevel.Low, result.Level);
        Assert.Empty(result.Signals);
    }

    // Entrega abaixo de 60% é o sinal clássico de soft-ban: o número segue
    // "Ativo" mas as mensagens não chegam (ack 1 nunca vira 2).
    [Fact]
    public void DeliveryBelow60Percent_ScoresThirtyPoints()
    {
        var result = NumberHealth.Evaluate(Baseline() with { DeliveryConsidered = 10, DeliveryMissing = 5 });

        var signal = Assert.Single(result.Signals);
        Assert.Equal("delivery", signal.Key);
        Assert.Equal("50%", signal.Value);
        Assert.Equal(30, signal.Points);
        Assert.Equal(HealthLevel.Medium, result.Level);
    }

    // Menos de 5 enviadas não formam amostra: uma única mensagem perdida não
    // pode gritar sozinha.
    [Fact]
    public void DeliveryWithTinySample_IsIgnored()
    {
        var result = NumberHealth.Evaluate(Baseline() with { DeliveryConsidered = 4, DeliveryMissing = 4 });

        Assert.Empty(result.Signals);
    }

    // Restrição 463 + ban somam 65 pontos: nível alto, com os dois sinais listados.
    [Fact]
    public void RestrictionPlusBan_IsHighWithBothSignals()
    {
        var result = NumberHealth.Evaluate(Baseline() with { SendRestricted = true, BanEvents = 1 });

        Assert.Equal(65, result.Score);
        Assert.Equal(HealthLevel.High, result.Level);
        Assert.Contains(result.Signals, s => s.Key == "sendRestriction");
        Assert.Contains(result.Signals, s => s.Key == "ban" && s.Points == 40);
    }

    // Tudo errado ao mesmo tempo satura em 100 e vira crítico — o score não passa do teto.
    [Fact]
    public void EverythingWrong_SaturatesAtOneHundred()
    {
        var result = NumberHealth.Evaluate(Baseline() with
        {
            DeliveryConsidered = 20,
            DeliveryMissing = 15,
            InboundConversations = 10,
            InboundConversationsReplied = 0,
            OutboundConversations = 30,
            DisconnectionsLast24h = 8,
            NewContactsPerDay = 60,
            SendRestricted = true,
            BanEvents = 2,
        });

        Assert.Equal(100, result.Score);
        Assert.Equal(HealthLevel.Critical, result.Level);
    }

    // Quem só dispara (mais de 50% das conversas iniciadas por nós) tem o perfil
    // do vetor nº 1 de ban e ganha o sinal de disparos.
    [Fact]
    public void MostlyOutboundConversations_FlagsOutboundShare()
    {
        var result = NumberHealth.Evaluate(Baseline() with
        {
            InboundConversations = 2,
            OutboundConversations = 8,
        });

        Assert.Contains(result.Signals, s => s.Key == "outboundShare" && s.Points == 15);
    }
}
