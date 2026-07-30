using ClosedXML.Excel;

namespace MonitorVendas.Api.Features.ReportExport;

// As abas de IA ficam separadas das métricas de propósito: fato medido e leitura
// de modelo não podem morar na mesma tabela sem o leitor saber qual é qual.
public static class AiSheetsWriter
{
    public const string ConversationsSheet = "IA — Conversas";
    public const string SellersSheet = "IA — Vendedores";

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
        "Observação",
    ];

    private static readonly double[] Widths =
        [22, 18, 24, 16, 16, 16, 20, 20, 12, 14, 40, 20, 14, 14, 30, 12, 28, 40, 22, 46, 28, 26];

    private static readonly XLColor Attention = XLColor.FromHtml("#F6D9DE");

    public static void Write(XLWorkbook workbook, ReportExportData data, TimeZoneInfo timeZone)
    {
        WriteConversations(workbook, data, timeZone);
        WriteSellers(workbook, data);
    }

    private static void WriteConversations(XLWorkbook workbook, ReportExportData data, TimeZoneInfo timeZone)
    {
        var sheet = workbook.Worksheets.Add(ConversationsSheet);

        for (var column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];

        ReportWorkbookWriter.StyleHeader(sheet.Range(1, 1, 1, Headers.Length));

        sheet.Column(2).Style.NumberFormat.Format = "@";
        sheet.Column(4).Style.NumberFormat.Format = "@";
        sheet.Column(5).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        sheet.Column(6).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        sheet.Column(9).Style.NumberFormat.Format = "0%";

        sheet.Cell(2, 1).InsertData(data.AiRows.Select(row => new object?[]
        {
            row.SellerName,
            row.SellerNumber,
            row.ContactName,
            row.ContactPhone,
            ReportWorkbookWriter.Local(row.StartedAt, timeZone),
            ReportWorkbookWriter.Local(row.LastMessageAt, timeZone),
            row.RealOutcome ?? "—",
            row.AiStatus ?? "—",
            row.Confidence,
            row.AiStatus is null ? "—" : row.Divergent ? "Sim" : "Não",
            row.Evidence,
            FriendlyLossReason(row.LossReason),
            Flag(row.AskedForSale),
            Flag(row.IgnoredBuyingSignal),
            row.Objections,
            Flag(row.ShouldRecontact),
            row.RecontactReason,
            row.SuggestedMessage,
            row.Interest,
            row.Summary,
            row.ConductAlert,
            row.NotAnalyzedReason,
        }));

        HighlightDivergences(sheet, data);

        sheet.SheetView.FreezeRows(1);
        if (data.AiRows.Count > 0)
            sheet.Range(1, 1, data.AiRows.Count + 1, Headers.Length).SetAutoFilter();

        for (var column = 0; column < Widths.Length; column++)
            sheet.Column(column + 1).Width = Widths[column];
    }

    // Divergência e confiança baixa ficam marcadas: status errado com cara de
    // certeza é pior que status ausente.
    private static void HighlightDivergences(IXLWorksheet sheet, ReportExportData data)
    {
        for (var i = 0; i < data.AiRows.Count; i++)
        {
            var row = data.AiRows[i];
            if (row.Divergent)
                sheet.Cell(i + 2, 10).Style.Fill.BackgroundColor = Attention;

            if (row.Confidence is { } confidence && confidence < 0.5)
                sheet.Cell(i + 2, 9).Style.Fill.BackgroundColor = Attention;
        }
    }

    private static void WriteSellers(XLWorkbook workbook, ReportExportData data)
    {
        var sheet = workbook.Worksheets.Add(SellersSheet);
        var row = 1;

        foreach (var synthesis in data.Syntheses)
        {
            sheet.Cell(row, 1).Value = synthesis.SellerName;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Font.FontSize = 12;
            row++;

            if (synthesis.Error is { } error)
            {
                sheet.Cell(row, 1).Value = "Sem síntese";
                sheet.Cell(row, 2).Value = error;
                row += 2;
                continue;
            }

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

            row++;
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

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "Sim" : "Não";

    public static string? FriendlyLossReason(string? code) => code switch
    {
        null => null,
        "preco" => "Preço",
        "prazo_entrega" => "Prazo ou frete",
        "sumiu" => "Cliente sumiu",
        "comprou_concorrente" => "Comprou em outro lugar",
        "produto_indisponivel" => "Produto indisponível",
        "desconfianca" => "Desconfiança",
        "fora_do_publico" => "Fora do público",
        "vendedor_nao_respondeu" => "Vendedor não respondeu",
        "outro" => "Outro",
        _ => code,
    };
}
