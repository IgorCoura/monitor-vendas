using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Spreadsheet;
using MonitorVendas.Api.Features.ReportExport;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace MonitorVendas.Tests.ReportExport;

public class ChartInjectorTests
{
    private static MemoryStream BuildWorkbook()
    {
        using var workbook = new XLWorkbook();
        var data = workbook.AddWorksheet("Ranking");
        data.Cell(1, 1).Value = "Vendedor";
        data.Cell(1, 2).Value = "Vendas";
        data.Cell(1, 3).Value = "Conversas";
        for (var i = 0; i < 3; i++)
        {
            data.Cell(i + 2, 1).Value = $"Vendedor {i + 1}";
            data.Cell(i + 2, 2).Value = (i + 1) * 3;
            data.Cell(i + 2, 3).Value = (i + 1) * 10;
        }

        workbook.AddWorksheet("Gráficos");

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static ChartSpec Spec(ChartKind kind, params string[] valueRanges) =>
        new("Gráficos", "Ranking", $"Teste {kind}", kind, "$A$2:$A$4",
            [.. valueRanges.Select((range, i) => new ChartSeriesSpec($"${(char)('B' + i)}$1", range))],
            AnchorColumn: 0, AnchorRow: 0);

    // O gráfico injetado precisa existir como ChartPart de verdade, apontando para
    // as células — é o que permite editar a planilha depois.
    [Fact]
    public void Inject_CreatesANativeChartBoundToTheCells()
    {
        using var stream = BuildWorkbook();

        ChartInjector.Inject(stream, [Spec(ChartKind.Bar, "$B$2:$B$4")]);

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.Workbook.Descendants<Sheet>().First(s => s.Name == "Gráficos");
        var worksheetPart = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);

        var drawingsPart = worksheetPart.DrawingsPart!;
        var chartPart = Assert.Single(drawingsPart.ChartParts);
        var barChart = Assert.Single(chartPart.ChartSpace.Descendants<C.BarChart>());
        var series = Assert.Single(barChart.Descendants<C.BarChartSeries>());

        Assert.Equal("'Ranking'!$B$2:$B$4", series.Descendants<C.NumberReference>().Single().Formula!.Text);
        Assert.Equal("'Ranking'!$A$2:$A$4", series.Descendants<C.StringReference>().Last().Formula!.Text);
        Assert.NotNull(worksheetPart.Worksheet.Elements<Drawing>().SingleOrDefault());
    }

    // Duas séries ganham legenda e cores distintas da paleta; uma série sozinha não
    // leva legenda — o título já a identifica.
    [Fact]
    public void Inject_AddsLegendOnlyWhenThereIsMoreThanOneSeries()
    {
        using var single = BuildWorkbook();
        ChartInjector.Inject(single, [Spec(ChartKind.Bar, "$B$2:$B$4")]);

        using var multiple = BuildWorkbook();
        ChartInjector.Inject(multiple, [Spec(ChartKind.Line, "$B$2:$B$4", "$C$2:$C$4")]);

        Assert.Empty(ChartSpaceOf(single).Descendants<C.Legend>());

        var space = ChartSpaceOf(multiple);
        Assert.Single(space.Descendants<C.Legend>());
        Assert.Equal(2, space.Descendants<C.LineChartSeries>().Count());
        Assert.Equal(
            ["C25E77", "4C86C6"],
            space.Descendants<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>().Select(c => c.Val!.Value ?? "").ToArray());
    }

    // Vários gráficos na mesma aba compartilham um único desenho, cada um com seu
    // ChartPart — é assim que o Excel espera encontrar.
    [Fact]
    public void Inject_PutsSeveralChartsInTheSameSheet()
    {
        using var stream = BuildWorkbook();

        ChartInjector.Inject(stream, [
            Spec(ChartKind.Bar, "$B$2:$B$4"),
            Spec(ChartKind.Line, "$C$2:$C$4") with { AnchorRow = 16 },
        ]);

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.Workbook.Descendants<Sheet>().First(s => s.Name == "Gráficos");
        var worksheetPart = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);

        Assert.Equal(2, worksheetPart.DrawingsPart!.ChartParts.Count());
    }

    // O arquivo precisa passar no validador do OpenXML: fora do schema, o Excel
    // acusa "conteúdo ilegível" e a exportação inteira vira lixo.
    [Fact]
    public void Inject_ProducesASchemaValidFile()
    {
        using var stream = BuildWorkbook();

        ChartInjector.Inject(stream, [
            Spec(ChartKind.Bar, "$B$2:$B$4"),
            Spec(ChartKind.Line, "$B$2:$B$4", "$C$2:$C$4") with { AnchorRow = 16 },
        ]);

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document).ToList();

        Assert.Empty(errors.Select(e => $"{e.Path?.XPath}: {e.Description}"));
    }

    // A planilha continua legível pelo ClosedXML depois da injeção: os dados não
    // podem ser corrompidos pelo pós-processamento.
    [Fact]
    public void Inject_KeepsTheWorkbookReadable()
    {
        using var stream = BuildWorkbook();

        ChartInjector.Inject(stream, [Spec(ChartKind.Bar, "$B$2:$B$4")]);

        stream.Position = 0;
        using var reopened = new XLWorkbook(stream);
        Assert.Equal("Vendedor 1", reopened.Worksheet("Ranking").Cell(2, 1).GetString());
        Assert.Equal(9, reopened.Worksheet("Ranking").Cell(4, 2).GetValue<int>());
    }

    private static C.ChartSpace ChartSpaceOf(Stream stream)
    {
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.Workbook.Descendants<Sheet>().First(s => s.Name == "Gráficos");
        var worksheetPart = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);

        return worksheetPart.DrawingsPart!.ChartParts.Single().ChartSpace.CloneNode(true) as C.ChartSpace
            ?? throw new InvalidOperationException("Gráfico não encontrado.");
    }
}
