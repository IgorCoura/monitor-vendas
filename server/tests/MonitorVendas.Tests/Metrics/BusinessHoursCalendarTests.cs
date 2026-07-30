using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Tests.Metrics;

public class BusinessHoursCalendarTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly BusinessHoursCalendar Calendar = new(SaoPaulo);

    // Converte horário local de São Paulo (UTC-3, sem horário de verão) para UTC.
    private static DateTime Local(int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 7, day, hour, minute, 0, DateTimeKind.Unspecified), SaoPaulo);

    // Intervalo inteiramente dentro do expediente conta na íntegra (10h→12h = 2h).
    [Fact]
    public void WithinBusinessHours_CountsFullInterval()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 10), Local(1, 12));
        Assert.Equal(TimeSpan.FromHours(2), elapsed);
    }

    // Mensagem às 20h só começa a contar às 9h do dia seguinte (20h→10h = 1h útil).
    [Fact]
    public void AfterHours_ClockStartsNextMorning()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 20), Local(2, 10));
        Assert.Equal(TimeSpan.FromHours(1), elapsed);
    }

    // Intervalo atravessando a noite soma só as janelas úteis (17h→10h = 1h + 1h).
    [Fact]
    public void Overnight_SumsOnlyBusinessWindows()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 17), Local(2, 10));
        Assert.Equal(TimeSpan.FromHours(2), elapsed);
    }

    // Início antes das 9h é aparado para as 9h (7h→9h30 = 30min).
    [Fact]
    public void BeforeOpening_ClampsToNine()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 7), Local(1, 9, 30));
        Assert.Equal(TimeSpan.FromMinutes(30), elapsed);
    }

    // Vários dias completos: cada dia útil contribui 9h (3 dias cheios = 27h).
    [Fact]
    public void MultipleDays_NineHoursPerDay()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 9), Local(3, 18));
        Assert.Equal(TimeSpan.FromHours(27), elapsed);
    }

    // Fim antes do início devolve zero, nunca negativo.
    [Fact]
    public void InvertedInterval_ReturnsZero()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(2, 10), Local(1, 10));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    // Downtime dentro do intervalo é descontado do tempo útil (10h→14h com queda 11h→12h = 3h).
    [Fact]
    public void Downtime_IsSubtracted()
    {
        var downtimes = new[] { new DowntimeInterval(Local(1, 11), Local(1, 12)) };
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 10), Local(1, 14), downtimes);
        Assert.Equal(TimeSpan.FromHours(3), elapsed);
    }

    // Downtime fora do expediente (madrugada) não desconta nada.
    [Fact]
    public void DowntimeOutsideBusinessHours_SubtractsNothing()
    {
        var downtimes = new[] { new DowntimeInterval(Local(1, 22), Local(2, 6)) };
        var elapsed = Calendar.BusinessTimeBetween(Local(1, 17), Local(2, 10), downtimes);
        Assert.Equal(TimeSpan.FromHours(2), elapsed);
    }

    // Sábado (4/jul/2026) tem janela curta 9h–13h: 10h→15h conta só 3h.
    [Fact]
    public void Saturday_UsesShortWindow()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(4, 10), Local(4, 15));
        Assert.Equal(TimeSpan.FromHours(3), elapsed);
    }

    // Com o sábado desativado na config, o dia inteiro não conta.
    [Fact]
    public void SaturdayDisabled_CountsZero()
    {
        var calendar = new BusinessHoursCalendar(SaoPaulo, saturdayEnabled: false);
        var elapsed = calendar.BusinessTimeBetween(Local(4, 10), Local(4, 15));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    // Domingo (5/jul/2026) nunca conta como tempo útil.
    [Fact]
    public void Sunday_CountsZero()
    {
        var elapsed = Calendar.BusinessTimeBetween(Local(5, 10), Local(5, 15));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    // Feriado cadastrado zera o dia inteiro (sexta 3/jul como feriado).
    [Fact]
    public void Holiday_CountsZero()
    {
        var calendar = new BusinessHoursCalendar(SaoPaulo, holidays: new HashSet<DateOnly> { new(2026, 7, 3) });
        var elapsed = calendar.BusinessTimeBetween(Local(3, 10), Local(3, 15));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    // Intervalo atravessando um feriado pula o dia: qui 18h→seg 10h com sexta
    // feriado = sábado 4h + segunda 1h.
    [Fact]
    public void IntervalSpanningHoliday_SkipsTheDay()
    {
        var calendar = new BusinessHoursCalendar(SaoPaulo, holidays: new HashSet<DateOnly> { new(2026, 7, 3) });
        var elapsed = calendar.BusinessTimeBetween(Local(2, 18), Local(6, 10));
        Assert.Equal(TimeSpan.FromHours(5), elapsed);
    }
}
