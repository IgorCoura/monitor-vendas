using ClosedXML.Excel;

namespace MonitorVendas.Api.Features.Contacts;

public static class ContactWorkbookWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Headers =
    [
        "Cliente",
        "Número",
        "Primeira mensagem",
        "Última mensagem",
        "Desfecho",
        "Etiquetas",
        "Vendedor",
        "Número do vendedor",
        "Número banido?",
        "Situação do número",
    ];

    private static readonly double[] Widths = [26, 16, 18, 18, 18, 30, 20, 18, 16, 22];

    // Datas saem no fuso do relatório (Metrics:TimeZone): o banco guarda UTC, mas
    // quem abre a planilha lê horário local.
    public static byte[] Build(IReadOnlyList<ContactRowDto> rows, TimeZoneInfo timeZone)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Contatos");

        for (var column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];

        var header = sheet.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0DEDA");

        // Telefone é texto: sem isso o Excel transforma 5511999998888 em notação científica.
        sheet.Column(2).Style.NumberFormat.Format = "@";
        sheet.Column(8).Style.NumberFormat.Format = "@";
        sheet.Column(3).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        sheet.Column(4).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";

        // InsertData escreve em lote; célula a célula custa 4x em milhares de linhas.
        sheet.Cell(2, 1).InsertData(rows.Select(row => new object?[]
        {
            row.Name,
            row.Phone,
            ToLocal(row.FirstMessageAt, timeZone),
            ToLocal(row.LastMessageAt, timeZone),
            row.Outcome,
            string.Join(", ", row.Labels),
            row.SellerName,
            row.SellerNumber,
            BannedLabel(row.NumberStatus),
            StatusLabel(row.NumberStatus),
        }));

        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, rows.Count + 1, Headers.Length).SetAutoFilter();

        // Largura fixa: AdjustToContents mede o texto de cada célula e custa mais que
        // gerar a planilha inteira (1,9 s → 0,3 s em 3.600 linhas).
        for (var column = 0; column < Widths.Length; column++)
            sheet.Column(column + 1).Width = Widths[column];

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    private static string BannedLabel(string status) => status switch
    {
        "BannedPermanent" => "Sim (permanente)",
        "BannedTemporary" => "Sim (temporário)",
        _ => "Não",
    };

    private static string StatusLabel(string status) => status switch
    {
        "Active" => "Ativo",
        "Disconnected" => "Desconectado",
        "BannedTemporary" => "Banido temporariamente",
        "BannedPermanent" => "Banido permanentemente",
        _ => status,
    };
}
