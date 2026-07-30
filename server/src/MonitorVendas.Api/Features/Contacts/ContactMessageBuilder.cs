namespace MonitorVendas.Api.Features.Contacts;

// Monta as mensagens do envio: "Nome - 5511999998888", uma por linha, quebradas
// em blocos numerados que cabem no limite do WhatsApp.
public static class ContactMessageBuilder
{
    public static IReadOnlyList<string> Build(
        IReadOnlyList<ContactRowDto> rows,
        DateTime? fromUtc,
        DateTime? toUtc,
        TimeZoneInfo timeZone,
        int maxChars)
    {
        if (rows.Count == 0)
            return [];

        var period = FormatPeriod(fromUtc, toUtc, timeZone);
        var lines = rows.Select(Line).ToList();

        // O cabeçalho só é escrito no fim (a numeração depende de quantos blocos
        // saírem), então o espaço dele é reservado no pior caso.
        var budget = Math.Max(maxChars - Header(999, 999, period).Length, 1);

        var blocks = new List<List<string>>();
        var current = new List<string>();
        var length = 0;

        foreach (var line in lines)
        {
            var cost = line.Length + 1;
            if (current.Count > 0 && length + cost > budget)
            {
                blocks.Add(current);
                current = [];
                length = 0;
            }

            current.Add(line);
            length += cost;
        }

        if (current.Count > 0)
            blocks.Add(current);

        return [.. blocks.Select((block, index) =>
            $"{Header(index + 1, blocks.Count, period)}\n\n{string.Join('\n', block)}")];
    }

    // Contato sem nome salvo sairia como "5511999998888 - 5511999998888".
    private static string Line(ContactRowDto row) =>
        row.Name == row.Phone ? row.Phone : $"{row.Name} - {row.Phone}";

    private static string Header(int part, int total, string period)
    {
        var counter = total > 1 ? $" ({part}/{total})" : string.Empty;
        return period.Length == 0 ? $"Contatos{counter}" : $"Contatos{counter} — {period}";
    }

    private static string FormatPeriod(DateTime? fromUtc, DateTime? toUtc, TimeZoneInfo timeZone)
    {
        var from = Local(fromUtc, timeZone);
        var to = Local(toUtc, timeZone);

        return (from, to) switch
        {
            ({ } start, { } end) => $"{start:dd/MM} a {end:dd/MM}",
            ({ } start, null) => $"desde {start:dd/MM}",
            (null, { } end) => $"até {end:dd/MM}",
            _ => string.Empty,
        };
    }

    private static DateTime? Local(DateTime? utc, TimeZoneInfo timeZone) =>
        utc is null ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), timeZone);
}
