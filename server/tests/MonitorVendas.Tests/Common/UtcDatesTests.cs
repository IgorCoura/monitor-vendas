using MonitorVendas.Api.Common;

namespace MonitorVendas.Tests.Common;

// Toda data de query string passa por aqui antes de virar filtro. Ler um horário
// local como UTC (ou o contrário) desloca o período em 3 horas e o relatório sai
// com o dia errado.
public class UtcDatesTests
{
    // Data sem fuso (o formato que a tela manda) é tratada como UTC, sem conversão.
    [Fact]
    public void Unspecified_IsAssumedUtc()
    {
        var value = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Unspecified);

        var utc = UtcDates.ToUtc(value);

        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
        Assert.Equal(12, utc.Value.Hour);
    }

    // Data com fuso local é convertida, não reinterpretada.
    [Fact]
    public void Local_IsConverted()
    {
        var value = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Local);

        var utc = UtcDates.ToUtc(value);

        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
        Assert.Equal(value.ToUniversalTime(), utc.Value);
    }

    // Data que já é UTC passa intacta; ausência de data continua ausente (não vira
    // "agora", que silenciosamente filtraria o período inteiro).
    [Fact]
    public void UtcAndNull_PassThrough()
    {
        var value = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(value, UtcDates.ToUtc(value));
        Assert.Null(UtcDates.ToUtc(null));
    }
}
