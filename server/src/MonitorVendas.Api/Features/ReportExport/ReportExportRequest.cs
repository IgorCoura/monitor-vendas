using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

// O que o usuário escolheu na tela. Listas vazias significam "tudo", para o
// pedido mínimo continuar produzindo um relatório completo.
public sealed record ReportExportRequest(
    DateTime From,
    DateTime To,
    IReadOnlyList<Guid> SellerIds,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string> Charts,
    bool IncludeNumbers)
{
    public static ReportExportRequest Empty(DateTime from, DateTime to) =>
        new(from, to, [], [], [], true);
}

public sealed record ReportExportData(
    DateTime From,
    DateTime To,
    IReadOnlyList<RankingEntryDto> Ranking,
    IReadOnlyList<SellerReportDto> SellerReports);
