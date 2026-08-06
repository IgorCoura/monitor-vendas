using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.ReportExport;

namespace MonitorVendas.Tests.ReportExport;

// O "Resumo" da planilha tem de sair pela MESMA regra das linhas por vendedor que
// estão na aba ao lado. Enquanto a espera do vendedor era média das médias diárias
// e a do time era média ponderada por amostras, as duas abas discordavam.
public class TeamTotalsTests
{
    private static readonly DateOnly Day1 = new(2026, 7, 1);
    private static readonly DateOnly Day2 = new(2026, 7, 2);

    private static MetricsDto Metrics(params ResponseWaitDayDto[] waitDays) =>
        new(0, 0, 0, 0, 0, null, null, null, null, null,
            waitDays.Sum(d => d.Count), waitDays,
            0, 0, null, null, null, 0, null, null, null, null,
            0, null, null, 0, 0, 0, []);

    private static RankingEntryDto Seller(string name, params ResponseWaitDayDto[] waitDays) =>
        new(Guid.NewGuid(), name, Metrics(waitDays));

    // Dois vendedores no MESMO dia viram um dia só: 1 resposta de 10 min e 3 de
    // 30 min dão média 25 no dia 1 (100 ÷ 4), não duas médias separadas. Com o dia
    // 2 valendo 5, a média do período é (25 + 5) ÷ 2 = 15.
    [Fact]
    public void Of_CombinesSellersByDay_ThenAveragesTheDays()
    {
        var ana = Seller("Ana",
            new ResponseWaitDayDto(Day1, Count: 1, SumMinutes: 10, MinMinutes: 10, MaxMinutes: 10),
            new ResponseWaitDayDto(Day2, Count: 1, SumMinutes: 5, MinMinutes: 5, MaxMinutes: 5));

        var bruno = Seller("Bruno",
            new ResponseWaitDayDto(Day1, Count: 3, SumMinutes: 90, MinMinutes: 20, MaxMinutes: 40));

        var totals = TeamTotals.Of([ana, bruno]);

        Assert.Equal(5, totals.ResponseSamplesCount);
        Assert.Equal(5, totals.MinResponseMinutes);
        Assert.Equal(40, totals.MaxResponseMinutes);
        Assert.Equal(15, totals.AvgResponseMinutes);
    }

    // Vendedor sem resposta nenhuma não entra como zero — arrastaria a média do
    // time para baixo inventando um dia perfeito que não existiu.
    [Fact]
    public void Of_IgnoresSellersWithoutSamples()
    {
        var ana = Seller("Ana", new ResponseWaitDayDto(Day1, 2, 40, 15, 25));
        var semNada = Seller("Bruno");

        var totals = TeamTotals.Of([ana, semNada]);

        Assert.Equal(2, totals.ResponseSamplesCount);
        Assert.Equal(20, totals.AvgResponseMinutes);
        Assert.Equal(15, totals.MinResponseMinutes);
        Assert.Equal(25, totals.MaxResponseMinutes);
    }

    // Time inteiro sem resposta: "—", não zero.
    [Fact]
    public void Of_WithNoSamplesAtAll_ReportsNothing()
    {
        var totals = TeamTotals.Of([Seller("Ana"), Seller("Bruno")]);

        Assert.Equal(0, totals.ResponseSamplesCount);
        Assert.Null(totals.AvgResponseMinutes);
        Assert.Null(totals.MinResponseMinutes);
        Assert.Null(totals.MaxResponseMinutes);
    }
}
