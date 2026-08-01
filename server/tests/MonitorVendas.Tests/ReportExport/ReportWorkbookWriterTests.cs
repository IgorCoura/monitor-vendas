using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Ai.Export;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.ReportExport;

namespace MonitorVendas.Tests.ReportExport;

public class ReportWorkbookWriterTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTime From = new(2026, 7, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc);

    private static MetricsDto Metrics(int started, int answered, int sales = 0, double? median = null) =>
        new(started, answered, 0, 0, 0,
            started > 0 ? (double)answered / started : null,
            median, null, null, null, 0,
            0, 0, null, null, null,
            sales, answered > 0 ? (double)sales / answered : null, null, null, null,
            8, null, 100, 0,
            [new OutcomeMetricDto("sale", "Vendas", sales, null, null),
             new OutcomeMetricDto("lost", "Clientes perdidos", 1, null, null)]);

    private static ReportExportData Data() =>
        new(From, To,
            [
                new RankingEntryDto(Guid.NewGuid(), "Ana", Metrics(10, 10, 4, median: 3)),
                new RankingEntryDto(Guid.NewGuid(), "Bruno", Metrics(90, 0)),
            ],
            []);

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    // A seleção da tela decide as abas: com gráficos marcados, a planilha ganha a
    // aba deles.
    [Fact]
    public void Build_CreatesOnlyTheSelectedSheets()
    {
        var request = ReportExportRequest.Empty(From, To) with { Charts = ["sales"] };

        using var workbook = Open(ReportWorkbookWriter.Build(Data(), request, SaoPaulo));

        Assert.True(workbook.Worksheets.Contains("Resumo"));
        Assert.True(workbook.Worksheets.Contains("Ranking"));
        Assert.True(workbook.Worksheets.Contains("Gráficos"));
    }

    // Escolher métricas restringe as colunas do ranking — é o filtro do usuário
    // valendo dentro do arquivo, não só na tela.
    [Fact]
    public void Build_LimitsColumnsToTheChosenMetrics()
    {
        var request = ReportExportRequest.Empty(From, To) with { Metrics = ["sales", "conversationsStarted"] };

        using var workbook = Open(ReportWorkbookWriter.Build(Data(), request, SaoPaulo));
        var ranking = workbook.Worksheet("Ranking");

        Assert.Equal("Vendedor", ranking.Cell(1, 1).GetString());
        Assert.Equal("Conversas iniciadas", ranking.Cell(1, 2).GetString());
        Assert.Equal("Vendas", ranking.Cell(1, 3).GetString());
        Assert.True(ranking.Cell(1, 4).IsEmpty());
        Assert.Equal("Ana", ranking.Cell(2, 1).GetString());
        Assert.Equal(4, ranking.Cell(2, 3).GetValue<int>());
    }

    // O total do time recalcula a taxa a partir das somas. Média de taxas daria
    // 50% aqui, e o número certo é 10%.
    [Fact]
    public void Build_RecomputesTeamRatesFromSums()
    {
        var request = ReportExportRequest.Empty(From, To) with { Metrics = ["responseRate"] };

        using var workbook = Open(ReportWorkbookWriter.Build(Data(), request, SaoPaulo));
        var summary = workbook.Worksheet("Resumo");
        var row = summary.RowsUsed().Single(r => r.Cell(1).GetString() == "Taxa de resposta");

        Assert.Equal(0.1, row.Cell(2).GetValue<double>(), 5);
    }

    // Mediana não soma entre vendedores: some da linha do time em vez de virar
    // zero, que seria um número errado com cara de certo.
    [Fact]
    public void Build_MarksNonSummableMetricsAsUnavailable()
    {
        var request = ReportExportRequest.Empty(From, To) with { Metrics = ["medianFirstResponseMinutes"] };

        using var workbook = Open(ReportWorkbookWriter.Build(Data(), request, SaoPaulo));
        var summary = workbook.Worksheet("Resumo");
        var row = summary.RowsUsed().Single(r => r.Cell(1).GetString().StartsWith("Mediana"));

        Assert.Equal("—", row.Cell(2).GetString());
    }

    // A planilha completa, com gráficos, precisa passar no validador do OpenXML —
    // senão o Excel acusa arquivo corrompido.
    [Fact]
    public void Build_ProducesASchemaValidFile()
    {
        var request = ReportExportRequest.Empty(From, To) with
        {
            Charts = ["sales", "conversationsStarted"],
        };

        var bytes = ReportWorkbookWriter.Build(Data(), request, SaoPaulo);

        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}")
            .ToList();

        Assert.Empty(errors);
    }
}
