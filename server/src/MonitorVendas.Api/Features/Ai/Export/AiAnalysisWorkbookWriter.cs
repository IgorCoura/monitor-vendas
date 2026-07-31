using ClosedXML.Excel;

namespace MonitorVendas.Api.Features.Ai.Export;

// A planilha das leituras já feitas. Não chama IA nenhuma: é o que está no banco
// virando arquivo. Fato medido e leitura de modelo continuam em abas separadas —
// o leitor precisa saber qual é qual.
public static class AiAnalysisWorkbookWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string AnalysesSheet = "Análises";
    public const string SynthesesSheet = "Sínteses";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F0DEDA");
    private static readonly XLColor Attention = XLColor.FromHtml("#F6D9DE");

    private static readonly string[] Headers =
    [
        "Vendedor",
        "Número do vendedor",
        "Cliente",
        "Telefone",
        "Início",
        "Última mensagem",
        "Desfecho (etiqueta)",
        "Status (IA)",
        "Confiança",
        "Divergência",
        "Evidência",
        "Motivo da perda",
        "Pediu a venda?",
        "Sinal ignorado?",
        "Objeções",
        "Recontatar?",
        "Motivo do recontato",
        "Mensagem sugerida",
        "Interesse",
        "Resumo",
        "Alerta de conduta",
        "Modelo",
        "Analisado em",
        "Versões",
        "Áudios ouvidos",
    ];

    private static readonly double[] Widths =
        [22, 18, 24, 16, 16, 16, 20, 20, 12, 14, 40, 20, 14, 14, 30, 12, 28, 40, 22, 46, 28, 18, 16, 10, 16];

    public static byte[] Build(
        IReadOnlyList<AiAnalysisRowDto> rows,
        IReadOnlyList<AiSynthesisDto> syntheses,
        TimeZoneInfo timeZone)
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            WriteAnalyses(workbook, rows, timeZone);
            WriteSyntheses(workbook, syntheses, timeZone);
            workbook.SaveAs(stream);
        }

        return stream.ToArray();
    }

    private static void WriteAnalyses(XLWorkbook workbook, IReadOnlyList<AiAnalysisRowDto> rows, TimeZoneInfo timeZone)
    {
        var sheet = workbook.Worksheets.Add(AnalysesSheet);

        for (var column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];

        StyleHeader(sheet.Range(1, 1, 1, Headers.Length));

        // Telefone é texto: como número o Excel vira notação científica.
        sheet.Column(2).Style.NumberFormat.Format = "@";
        sheet.Column(4).Style.NumberFormat.Format = "@";
        sheet.Column(5).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        sheet.Column(6).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        sheet.Column(9).Style.NumberFormat.Format = "0%";
        sheet.Column(23).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";

        sheet.Cell(2, 1).InsertData(rows.Select(row => new object?[]
        {
            row.SellerName,
            row.SellerNumber,
            row.ContactName,
            row.ContactPhone,
            Local(row.StartedAt, timeZone),
            Local(row.LastMessageAt, timeZone),
            row.RealOutcome ?? "—",
            row.AiStatus ?? "—",
            row.Confidence,
            row.Divergent ? "Sim" : "Não",
            row.Evidence,
            row.LossReason,
            Flag(row.AskedForSale),
            Flag(row.IgnoredBuyingSignal),
            row.Objections,
            Flag(row.ShouldRecontact),
            row.RecontactReason,
            row.SuggestedMessage,
            row.Interest,
            row.Summary,
            row.ConductAlert,
            row.Model,
            Local(row.AnalyzedAt, timeZone),
            row.Versions,
            AudioLabel(row),
        }));

        HighlightDivergences(sheet, rows);

        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
            sheet.Range(1, 1, rows.Count + 1, Headers.Length).SetAutoFilter();

        for (var column = 0; column < Widths.Length; column++)
            sheet.Column(column + 1).Width = Widths[column];
    }

    // Conversa sem áudio não ganha rótulo; com áudio, a razão mostra se o modelo
    // ouviu tudo. Análise surda com cara de completa foi o que fez uma falha de
    // download passar por erro da IA.
    private static string? AudioLabel(AiAnalysisRowDto row) =>
        row.AudioExpected == 0 ? null : $"{row.AudioAttached} de {row.AudioExpected}";

    // Divergência, confiança baixa e áudio faltando ficam marcados: análise
    // incompleta com cara de certeza é pior que análise ausente.
    private static void HighlightDivergences(IXLWorksheet sheet, IReadOnlyList<AiAnalysisRowDto> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Divergent)
                sheet.Cell(i + 2, 10).Style.Fill.BackgroundColor = Attention;

            if (rows[i].Confidence < 0.5)
                sheet.Cell(i + 2, 9).Style.Fill.BackgroundColor = Attention;

            if (rows[i].AudioExpected > rows[i].AudioAttached)
                sheet.Cell(i + 2, 25).Style.Fill.BackgroundColor = Attention;
        }
    }

    private static void WriteSyntheses(XLWorkbook workbook, IReadOnlyList<AiSynthesisDto> syntheses, TimeZoneInfo timeZone)
    {
        var sheet = workbook.Worksheets.Add(SynthesesSheet);
        var row = 1;

        foreach (var synthesis in syntheses)
        {
            sheet.Cell(row, 1).Value = synthesis.SellerName;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Font.FontSize = 12;
            // Parecer que descreve leituras já mudadas precisa dizer isso na cara.
            sheet.Cell(row, 2).Value = synthesis.Stale ? "Desatualizada" : "Atual";
            if (synthesis.Stale)
                sheet.Cell(row, 2).Style.Fill.BackgroundColor = Attention;
            row++;

            if (synthesis.Overview is { } overview)
            {
                sheet.Cell(row, 1).Value = "Visão geral";
                sheet.Cell(row, 2).Value = overview;
                row++;
            }

            row = WriteList(sheet, row, "Pontos fortes", synthesis.Strengths);
            row = WriteList(sheet, row, "A melhorar", synthesis.Improvements);

            if (synthesis.DominantLossPattern is { } pattern)
            {
                sheet.Cell(row, 1).Value = "Padrão de perda";
                sheet.Cell(row, 2).Value = pattern;
                row++;
            }

            if (synthesis.TrainingSuggestion is { } training)
            {
                sheet.Cell(row, 1).Value = "Treinamento sugerido";
                sheet.Cell(row, 2).Value = training;
                row++;
            }

            sheet.Cell(row, 1).Value = "Gerada em";
            sheet.Cell(row, 2).Value =
                $"{Local(synthesis.CreatedAt, timeZone):dd/MM/yyyy HH:mm} · {synthesis.ConversationsCount} conversas · {synthesis.Model}";
            row += 2;
        }

        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 110;
        sheet.Column(2).Style.Alignment.WrapText = true;
    }

    private static int WriteList(IXLWorksheet sheet, int row, string label, IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            sheet.Cell(row, 1).Value = i == 0 ? label : string.Empty;
            sheet.Cell(row, 2).Value = items[i];
            row++;
        }

        return row;
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = HeaderFill;
    }

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "Sim" : "Não";

    private static DateTime Local(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);
}
