using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

// Monta a planilha do relatório. Nenhuma métrica é recalculada aqui: consome o
// mesmo ReportQueries da tela (cache, agregado, horário comercial), porque
// cálculo próprio divergiria da tela sem ninguém perceber.
public sealed class ReportExportBuilder(ReportQueries queries, IOptions<MetricsOptions> metricsOptions)
{
    public async Task<(byte[] File, string FileName)> BuildAsync(
        ReportExportRequest request,
        CancellationToken ct = default)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(metricsOptions.Value.TimeZone);

        var ranking = await queries.GetRankingAsync(request.From, request.To, ct);
        if (request.SellerIds.Count > 0)
        {
            var wanted = request.SellerIds.ToHashSet();
            ranking = [.. ranking.Where(r => wanted.Contains(r.SellerId))];
        }

        var sellerReports = new List<SellerReportDto>();
        if (request.IncludeNumbers)
        {
            foreach (var entry in ranking)
            {
                var report = await queries.GetSellerReportAsync(entry.SellerId, request.From, request.To, ct);
                if (report is not null)
                    sellerReports.Add(report);
            }
        }

        var data = new ReportExportData(request.From, request.To, ranking, sellerReports);

        return (ReportWorkbookWriter.Build(data, request, timeZone), FileNameFor(request, timeZone));
    }

    internal static string FileNameFor(ReportExportRequest request, TimeZoneInfo timeZone) =>
        $"relatorio-{ReportWorkbookWriter.Local(request.From, timeZone):yyyy-MM-dd}" +
        $"-a-{ReportWorkbookWriter.Local(request.To, timeZone):yyyy-MM-dd}.xlsx";
}
