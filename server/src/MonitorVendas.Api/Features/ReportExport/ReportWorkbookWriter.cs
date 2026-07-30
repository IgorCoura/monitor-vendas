using ClosedXML.Excel;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

public static class ReportWorkbookWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string SummarySheet = "Resumo";
    public const string RankingSheet = "Ranking";
    public const string ChartsSheet = "Gráficos";
    public const string NumbersSheet = "Por número";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F0DEDA");

    public static byte[] Build(ReportExportData data, ReportExportRequest request, TimeZoneInfo timeZone)
    {
        var outcomes = data.Ranking.FirstOrDefault()?.Metrics.Outcomes ?? [];
        var metrics = ReportMetricCatalog.Select(ReportMetricCatalog.Build(outcomes), request.Metrics);

        using var stream = new MemoryStream();

        using (var workbook = new XLWorkbook())
        {
            WriteSummary(workbook, data, metrics, timeZone);
            WriteRanking(workbook, data, metrics);

            var charts = BuildChartSpecs(data, metrics, request.Charts);
            if (charts.Count > 0)
                workbook.Worksheets.Add(ChartsSheet);

            if (request.IncludeNumbers && data.SellerReports.Count > 0)
                WriteNumbers(workbook, data, metrics);

            if (request.IncludeAi)
                AiSheetsWriter.Write(workbook, data, timeZone);

            workbook.SaveAs(stream);

            if (charts.Count > 0)
            {
                stream.Position = 0;
                ChartInjector.Inject(stream, charts);
            }
        }

        return stream.ToArray();
    }

    private static void WriteSummary(XLWorkbook workbook, ReportExportData data, IReadOnlyList<ReportMetric> metrics, TimeZoneInfo timeZone)
    {
        var sheet = workbook.Worksheets.Add(SummarySheet);
        var totals = TeamTotals.Of(data.Ranking);

        sheet.Cell(1, 1).Value = "Relatório de desempenho";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        sheet.Cell(2, 1).Value = "Período";
        sheet.Cell(2, 2).Value = $"{Local(data.From, timeZone):dd/MM/yyyy HH:mm} a {Local(data.To, timeZone):dd/MM/yyyy HH:mm}";
        sheet.Cell(3, 1).Value = "Vendedores";
        sheet.Cell(3, 2).Value = data.Ranking.Count;
        sheet.Cell(4, 1).Value = "Gerado em";
        sheet.Cell(4, 2).Value = Local(DateTime.UtcNow, timeZone).ToString("dd/MM/yyyy HH:mm");

        var row = 6;
        sheet.Cell(row, 1).Value = "Total do time";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;

        sheet.Cell(row, 1).Value = "Métrica";
        sheet.Cell(row, 2).Value = "Valor";
        StyleHeader(sheet.Range(row, 1, row, 2));
        row++;

        foreach (var metric in metrics)
        {
            sheet.Cell(row, 1).Value = metric.Label;
            var value = metric.Value(totals);
            if (value is not null)
            {
                sheet.Cell(row, 2).Value = value.Value;
                sheet.Cell(row, 2).Style.NumberFormat.Format = metric.Format;
            }
            else
            {
                // Grandeza que não soma entre vendedores (mediana) fica explícita em
                // vez de sair como zero — zero seria uma mentira mensurável.
                sheet.Cell(row, 2).Value = "—";
            }

            row++;
        }

        if (data.AiSummary is { } ai)
        {
            row++;
            sheet.Cell(row, 1).Value = "Análise por IA";
            sheet.Cell(row, 1).Style.Font.Bold = true;
            row++;
            foreach (var (label, value) in new (string, string)[]
                     {
                         ("Modelo", ai.Model),
                         ("Conversas analisadas agora", ai.Analyzed.ToString()),
                         ("Reaproveitadas do cache", ai.FromCache.ToString()),
                         ("Sem análise", ai.NotAnalyzed.ToString()),
                         ("Custo (R$)", ai.CostBrl.ToString("0.0000")),
                     })
            {
                sheet.Cell(row, 1).Value = label;
                sheet.Cell(row, 2).Value = value;
                row++;
            }

            sheet.Cell(row + 1, 1).Value =
                "As abas de IA são estimativa de um modelo de linguagem, não medição. O desfecho oficial continua sendo o da etiqueta.";
            sheet.Cell(row + 1, 1).Style.Font.Italic = true;
        }

        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 30;
    }

    private static void WriteRanking(XLWorkbook workbook, ReportExportData data, IReadOnlyList<ReportMetric> metrics)
    {
        var sheet = workbook.Worksheets.Add(RankingSheet);

        sheet.Cell(1, 1).Value = "Vendedor";
        for (var i = 0; i < metrics.Count; i++)
            sheet.Cell(1, i + 2).Value = metrics[i].Label;

        StyleHeader(sheet.Range(1, 1, 1, metrics.Count + 1));

        var row = 2;
        foreach (var entry in data.Ranking)
        {
            sheet.Cell(row, 1).Value = entry.Name;
            for (var i = 0; i < metrics.Count; i++)
            {
                var value = metrics[i].Value(entry.Metrics);
                if (value is null)
                    continue;

                sheet.Cell(row, i + 2).Value = value.Value;
                sheet.Cell(row, i + 2).Style.NumberFormat.Format = metrics[i].Format;
            }

            row++;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(1);
        if (data.Ranking.Count > 0)
            sheet.Range(1, 1, row - 1, metrics.Count + 1).SetAutoFilter();

        sheet.Column(1).Width = 26;
        for (var i = 0; i < metrics.Count; i++)
            sheet.Column(i + 2).Width = 18;
    }

    private static void WriteNumbers(XLWorkbook workbook, ReportExportData data, IReadOnlyList<ReportMetric> metrics)
    {
        var sheet = workbook.Worksheets.Add(NumbersSheet);

        sheet.Cell(1, 1).Value = "Vendedor";
        sheet.Cell(1, 2).Value = "Número";
        sheet.Cell(1, 3).Value = "Situação";
        for (var i = 0; i < metrics.Count; i++)
            sheet.Cell(1, i + 4).Value = metrics[i].Label;

        StyleHeader(sheet.Range(1, 1, 1, metrics.Count + 3));
        sheet.Column(2).Style.NumberFormat.Format = "@";

        var row = 2;
        foreach (var report in data.SellerReports)
        {
            foreach (var number in report.Numbers)
            {
                sheet.Cell(row, 1).Value = report.Name;
                sheet.Cell(row, 2).Value = number.Phone;
                sheet.Cell(row, 3).Value = StatusLabel(number.Status);

                for (var i = 0; i < metrics.Count; i++)
                {
                    var value = metrics[i].Value(number.Metrics);
                    if (value is null)
                        continue;

                    sheet.Cell(row, i + 4).Value = value.Value;
                    sheet.Cell(row, i + 4).Style.NumberFormat.Format = metrics[i].Format;
                }

                row++;
            }
        }

        sheet.SheetView.FreezeRows(1);
        if (row > 2)
            sheet.Range(1, 1, row - 1, metrics.Count + 3).SetAutoFilter();

        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 22;
        for (var i = 0; i < metrics.Count; i++)
            sheet.Column(i + 4).Width = 18;
    }

    // Um gráfico de barras por métrica escolhida, lendo direto da aba Ranking: o
    // usuário pode reordenar as linhas que o gráfico acompanha.
    private static List<ChartSpec> BuildChartSpecs(ReportExportData data, IReadOnlyList<ReportMetric> metrics, IReadOnlyList<string> chartKeys)
    {
        if (chartKeys.Count == 0 || data.Ranking.Count == 0)
            return [];

        var lastRow = data.Ranking.Count + 1;
        var categories = $"$A$2:$A${lastRow}";
        var specs = new List<ChartSpec>();
        var anchorRow = 1;

        foreach (var key in chartKeys)
        {
            var index = metrics.ToList().FindIndex(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;

            var column = ColumnLetter(index + 2);
            specs.Add(new ChartSpec(
                ChartsSheet,
                RankingSheet,
                metrics[index].Label,
                ChartKind.Bar,
                categories,
                [new ChartSeriesSpec($"${column}$1", $"${column}$2:${column}${lastRow}")],
                AnchorColumn: 1,
                AnchorRow: anchorRow));

            anchorRow += 17;
        }

        return specs;
    }

    internal static string ColumnLetter(int columnNumber)
    {
        var letters = string.Empty;
        while (columnNumber > 0)
        {
            var remainder = (columnNumber - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            columnNumber = (columnNumber - 1) / 26;
        }

        return letters;
    }

    internal static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = HeaderFill;
    }

    internal static DateTime Local(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    private static string StatusLabel(string status) => status switch
    {
        "Active" => "Ativo",
        "Disconnected" => "Desconectado",
        "BannedTemporary" => "Banido temporariamente",
        "BannedPermanent" => "Banido permanentemente",
        _ => status,
    };
}
