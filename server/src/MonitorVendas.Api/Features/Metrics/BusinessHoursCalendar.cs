namespace MonitorVendas.Api.Features.Metrics;

// Aritmética de tempo útil: seg–sex 9h–18h, sábado 9h–13h (desativável via
// config) e domingo sem expediente. Feriados cadastrados zeram o dia inteiro.
public sealed class BusinessHoursCalendar(
    TimeZoneInfo timeZone,
    int weekdayStartHour = 9,
    int weekdayEndHour = 18,
    bool saturdayEnabled = true,
    int saturdayStartHour = 9,
    int saturdayEndHour = 13,
    IReadOnlySet<DateOnly>? holidays = null)
{
    // O dia a que um instante pertence no fuso do relatório. É por ele que a espera
    // de resposta é consolidada — dia é conceito local, não UTC.
    public DateOnly LocalDayOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone));

    public TimeSpan BusinessTimeBetween(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            return TimeSpan.Zero;

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), timeZone);
        var toLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc), timeZone);

        var total = TimeSpan.Zero;
        for (var day = fromLocal.Date; day <= toLocal.Date; day = day.AddDays(1))
        {
            if (WindowFor(day) is not var (startHour, endHour))
                continue;

            var windowStart = day.AddHours(startHour);
            var windowEnd = day.AddHours(endHour);
            var start = fromLocal > windowStart ? fromLocal : windowStart;
            var end = toLocal < windowEnd ? toLocal : windowEnd;
            if (end > start)
                total += end - start;
        }

        return total;
    }

    // Tempo útil descontando janelas de indisponibilidade do número (ban/desconexão):
    // o relógio do vendedor não corre enquanto o canal está fora do ar.
    public TimeSpan BusinessTimeBetween(DateTime fromUtc, DateTime toUtc, IReadOnlyList<DowntimeInterval> mergedDowntimes)
    {
        var total = BusinessTimeBetween(fromUtc, toUtc);

        foreach (var downtime in mergedDowntimes)
        {
            var start = downtime.Start > fromUtc ? downtime.Start : fromUtc;
            var end = downtime.End < toUtc ? downtime.End : toUtc;
            if (end > start)
                total -= BusinessTimeBetween(start, end);
        }

        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }

    private (int Start, int End)? WindowFor(DateTime localDate)
    {
        if (holidays is not null && holidays.Contains(DateOnly.FromDateTime(localDate)))
            return null;

        return localDate.DayOfWeek switch
        {
            DayOfWeek.Sunday => null,
            DayOfWeek.Saturday => saturdayEnabled ? (saturdayStartHour, saturdayEndHour) : null,
            _ => (weekdayStartHour, weekdayEndHour)
        };
    }
}

public readonly record struct DowntimeInterval(DateTime Start, DateTime End);
