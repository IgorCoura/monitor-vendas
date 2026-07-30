using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MonitorVendas.Api.Features.Ai.Analysis;
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

    private static ReportExportData Data(
        IReadOnlyList<AiConversationRow>? aiRows = null,
        IReadOnlyList<SellerSynthesis>? syntheses = null) =>
        new(From, To,
            [
                new RankingEntryDto(Guid.NewGuid(), "Ana", Metrics(10, 10, 4, median: 3)),
                new RankingEntryDto(Guid.NewGuid(), "Bruno", Metrics(90, 0)),
            ],
            [],
            aiRows ?? [],
            syntheses ?? [],
            null);

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static AiConversationRow AiRow(string? realOutcome, string? aiStatus, bool divergent, double? confidence = 0.9) =>
        new("Ana", "5511900001111", "Maria", "5511977776666", From, To,
            realOutcome, aiStatus, confidence, divergent, "vou pagar amanhã", "preco",
            true, false, "achou caro", true, "prometeu e sumiu", "oi!", "kit", "resumo", null, null);

    // A seleção da tela decide as abas: sem IA marcada, a planilha não ganha abas
    // de IA; com gráficos, ganha a aba deles.
    [Fact]
    public void Build_CreatesOnlyTheSelectedSheets()
    {
        var request = ReportExportRequest.Empty(From, To) with { Charts = ["sales"] };

        using var workbook = Open(ReportWorkbookWriter.Build(Data(), request, SaoPaulo));

        Assert.True(workbook.Worksheets.Contains("Resumo"));
        Assert.True(workbook.Worksheets.Contains("Ranking"));
        Assert.True(workbook.Worksheets.Contains("Gráficos"));
        Assert.False(workbook.Worksheets.Contains("IA — Conversas"));
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

    // A coluna de divergência é o que justifica a aba: IA dizendo perdida onde
    // ninguém etiquetou precisa saltar aos olhos.
    [Fact]
    public void Build_FlagsDivergenceBetweenLabelAndAi()
    {
        var request = ReportExportRequest.Empty(From, To) with { IncludeAi = true };
        var data = Data([
            AiRow("Vendas", "Vendas", divergent: false),
            AiRow(null, "Clientes perdidos", divergent: true),
        ]);

        using var workbook = Open(ReportWorkbookWriter.Build(data, request, SaoPaulo));
        var sheet = workbook.Worksheet("IA — Conversas");

        Assert.Equal("Divergência", sheet.Cell(1, 10).GetString());
        Assert.Equal("Não", sheet.Cell(2, 10).GetString());
        Assert.Equal("Sim", sheet.Cell(3, 10).GetString());
        Assert.Equal("—", sheet.Cell(3, 7).GetString());
        Assert.Equal("Preço", sheet.Cell(3, 12).GetString());
    }

    // Conversa sem análise não some da planilha: sai com a observação do motivo.
    [Fact]
    public void Build_KeepsConversationsThatCouldNotBeAnalyzed()
    {
        var request = ReportExportRequest.Empty(From, To) with { IncludeAi = true };
        var skipped = AiRow(null, null, divergent: false, confidence: null) with
        {
            NotAnalyzedReason = "Saldo de IA insuficiente.",
        };

        using var workbook = Open(ReportWorkbookWriter.Build(Data([skipped]), request, SaoPaulo));
        var sheet = workbook.Worksheet("IA — Conversas");

        Assert.Equal("—", sheet.Cell(2, 8).GetString());
        Assert.Equal("Saldo de IA insuficiente.", sheet.Cell(2, 22).GetString());
    }

    // A síntese por vendedor sai como texto legível, com as evidências abaixo do nome.
    [Fact]
    public void Build_WritesSellerSynthesis()
    {
        var request = ReportExportRequest.Empty(From, To) with { IncludeAi = true };
        var synthesis = new SellerSynthesis(Guid.NewGuid(), "Ana", "amostra pequena",
            ["responde rápido"], ["não pede a venda"], "preço", "treinar fechamento", 0.01m, null);

        using var workbook = Open(ReportWorkbookWriter.Build(Data(syntheses: [synthesis]), request, SaoPaulo));
        var sheet = workbook.Worksheet("IA — Vendedores");

        Assert.Equal("Ana", sheet.Cell(1, 1).GetString());
        Assert.Equal("amostra pequena", sheet.Cell(2, 2).GetString());
        Assert.Equal("responde rápido", sheet.Cell(3, 2).GetString());
        Assert.Equal("não pede a venda", sheet.Cell(4, 2).GetString());
    }

    // A planilha completa, com gráficos e abas de IA, precisa passar no validador
    // do OpenXML — senão o Excel acusa arquivo corrompido.
    [Fact]
    public void Build_ProducesASchemaValidFile()
    {
        var request = ReportExportRequest.Empty(From, To) with
        {
            Charts = ["sales", "conversationsStarted"],
            IncludeAi = true,
        };

        var bytes = ReportWorkbookWriter.Build(Data([AiRow("Vendas", "Vendas", false)]), request, SaoPaulo);

        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}")
            .ToList();

        Assert.Empty(errors);
    }
}
