using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace MonitorVendas.Api.Features.ReportExport;

public enum ChartKind
{
    Bar,
    Line
}

public sealed record ChartSeriesSpec(string NameCell, string ValueRange);

public sealed record ChartSpec(
    string TargetSheet,
    string DataSheet,
    string Title,
    ChartKind Kind,
    string CategoryRange,
    IReadOnlyList<ChartSeriesSpec> Series,
    int AnchorColumn,
    int AnchorRow,
    int WidthCells = 8,
    int HeightCells = 15);

// ClosedXML não cria gráficos. Em vez de colar imagem morta, o .xlsx pronto é
// reaberto e o gráfico é injetado apontando para as células: quem recebe pode
// editar, reordenar e o gráfico acompanha.
public static class ChartInjector
{
    // Mesma ordem fixa da paleta do painel — cor seguindo a série, nunca o rank.
    private static readonly string[] Palette = ["C25E77", "4C86C6", "C67947", "8E6BAE", "4E9D57"];

    private const uint CategoryAxisId = 111_111_111;
    private const uint ValueAxisId = 222_222_222;

    public static void Inject(Stream xlsx, IReadOnlyList<ChartSpec> specs)
    {
        if (specs.Count == 0)
            return;

        using var document = SpreadsheetDocument.Open(xlsx, true);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("Planilha sem workbook part.");

        var chartId = 1u;

        foreach (var group in specs.GroupBy(s => s.TargetSheet, StringComparer.Ordinal))
        {
            var worksheetPart = FindWorksheet(workbookPart, group.Key);
            var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
            var anchors = new List<Xdr.TwoCellAnchor>();

            foreach (var spec in group)
            {
                var chartPart = drawingsPart.AddNewPart<ChartPart>();
                chartPart.ChartSpace = BuildChartSpace(spec);
                anchors.Add(BuildAnchor(spec, drawingsPart.GetIdOfPart(chartPart), chartId++));
            }

            drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing(anchors);
            AttachDrawing(worksheetPart, worksheetPart.GetIdOfPart(drawingsPart));
            worksheetPart.Worksheet.Save();
        }

        workbookPart.Workbook.Save();
    }

    private static WorksheetPart FindWorksheet(WorkbookPart workbookPart, string sheetName)
    {
        var sheet = workbookPart.Workbook.Descendants<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Aba '{sheetName}' não existe na planilha.");

        return (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
    }

    // O <drawing> tem lugar marcado no schema da aba: depois da configuração de
    // página e antes das tabelas. Fora de ordem, o Excel recusa o arquivo.
    private static void AttachDrawing(WorksheetPart worksheetPart, string relationshipId)
    {
        var worksheet = worksheetPart.Worksheet;
        var drawing = new Drawing { Id = relationshipId };
        var tableParts = worksheet.Elements<TableParts>().FirstOrDefault();

        if (tableParts is not null)
            worksheet.InsertBefore(drawing, tableParts);
        else
            worksheet.Append(drawing);
    }

    private static C.ChartSpace BuildChartSpace(ChartSpec spec)
    {
        var plotArea = new C.PlotArea(new C.Layout());
        plotArea.Append(spec.Kind == ChartKind.Bar ? BuildBarChart(spec) : BuildLineChart(spec));
        plotArea.Append(BuildCategoryAxis(), BuildValueAxis());

        var chart = new C.Chart(
            BuildTitle(spec.Title),
            new C.AutoTitleDeleted { Val = false },
            plotArea);

        // Série única não leva legenda: o título já diz o que é.
        if (spec.Series.Count > 1)
            chart.Append(new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }));

        chart.Append(new C.PlotVisibleOnly { Val = true });

        return new C.ChartSpace(new C.EditingLanguage { Val = "pt-BR" }, chart);
    }

    private static C.Title BuildTitle(string title) =>
        new(new C.ChartText(new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.RunProperties { Language = "pt-BR" }, new A.Text(title))))),
            new C.Overlay { Val = false });

    private static C.BarChart BuildBarChart(ChartSpec spec)
    {
        var chart = new C.BarChart(
            new C.BarDirection { Val = C.BarDirectionValues.Column },
            new C.BarGrouping { Val = C.BarGroupingValues.Clustered });

        for (var index = 0; index < spec.Series.Count; index++)
        {
            var series = spec.Series[index];
            chart.Append(new C.BarChartSeries(
                new C.Index { Val = (uint)index },
                new C.Order { Val = (uint)index },
                SeriesName(spec, series),
                new C.ChartShapeProperties(new A.SolidFill(Color(index))),
                new C.InvertIfNegative { Val = false },
                Categories(spec),
                Values(spec, series)));
        }

        chart.Append(new C.GapWidth { Val = 60 });
        chart.Append(new C.AxisId { Val = CategoryAxisId }, new C.AxisId { Val = ValueAxisId });

        return chart;
    }

    private static C.LineChart BuildLineChart(ChartSpec spec)
    {
        var chart = new C.LineChart(new C.Grouping { Val = C.GroupingValues.Standard });

        for (var index = 0; index < spec.Series.Count; index++)
        {
            var series = spec.Series[index];
            chart.Append(new C.LineChartSeries(
                new C.Index { Val = (uint)index },
                new C.Order { Val = (uint)index },
                SeriesName(spec, series),
                new C.ChartShapeProperties(new A.Outline(new A.SolidFill(Color(index))) { Width = 25_400 }),
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }, new C.Size { Val = 6 }),
                Categories(spec),
                Values(spec, series),
                new C.Smooth { Val = false }));
        }

        chart.Append(new C.ShowMarker { Val = true });
        chart.Append(new C.AxisId { Val = CategoryAxisId }, new C.AxisId { Val = ValueAxisId });

        return chart;
    }

    private static C.SeriesText SeriesName(ChartSpec spec, ChartSeriesSpec series) =>
        new(new C.StringReference { Formula = new C.Formula(Reference(spec.DataSheet, series.NameCell)) });

    private static C.CategoryAxisData Categories(ChartSpec spec) =>
        new(new C.StringReference { Formula = new C.Formula(Reference(spec.DataSheet, spec.CategoryRange)) });

    private static C.Values Values(ChartSpec spec, ChartSeriesSpec series) =>
        new(new C.NumberReference { Formula = new C.Formula(Reference(spec.DataSheet, series.ValueRange)) });

    private static string Reference(string sheet, string range) => $"'{sheet}'!{range}";

    private static A.RgbColorModelHex Color(int index) => new() { Val = Palette[index % Palette.Length] };

    private static C.CategoryAxis BuildCategoryAxis() =>
        new(new C.AxisId { Val = CategoryAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.CrossingAxis { Val = ValueAxisId });

    private static C.ValueAxis BuildValueAxis() =>
        new(new C.AxisId { Val = ValueAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.CrossingAxis { Val = CategoryAxisId });

    private static Xdr.TwoCellAnchor BuildAnchor(ChartSpec spec, string relationshipId, uint id) =>
        new(new Xdr.FromMarker(
                new Xdr.ColumnId(spec.AnchorColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(spec.AnchorRow.ToString()),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId((spec.AnchorColumn + spec.WidthCells).ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId((spec.AnchorRow + spec.HeightCells).ToString()),
                new Xdr.RowOffset("0")),
            new Xdr.GraphicFrame(
                new Xdr.NonVisualGraphicFrameProperties(
                    new Xdr.NonVisualDrawingProperties { Id = id + 1, Name = spec.Title },
                    new Xdr.NonVisualGraphicFrameDrawingProperties()),
                new Xdr.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
                new A.Graphic(new A.GraphicData(new C.ChartReference { Id = relationshipId })
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart",
                })),
            new Xdr.ClientData());
}
