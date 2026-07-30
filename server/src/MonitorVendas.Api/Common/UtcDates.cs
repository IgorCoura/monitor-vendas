namespace MonitorVendas.Api.Common;

public static class UtcDates
{
    // Data vinda da query string sem offset é tratada como UTC.
    public static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };
}
