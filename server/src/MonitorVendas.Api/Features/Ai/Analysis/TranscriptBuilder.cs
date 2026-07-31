using System.Text;
using System.Text.RegularExpressions;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Api.Features.Ai.Analysis;

public sealed record TranscriptMessage(
    MessageDirection Direction,
    DateTime TimestampUtc,
    string? Text,
    string Type,
    int? DurationSeconds = null,
    // Posição deste áudio entre os anexos enviados (1..N), na mesma ordem em que
    // eles vão na chamada. Nulo = não foi anexado, e aí o marcador é só um aviso
    // de que existiu áudio. Sem essa numeração o modelo recebe N blobs soltos e
    // não sabe a qual momento da conversa cada um pertence.
    int? AudioIndex = null);

// Monta o texto que vai para a IA. Nome e telefone do cliente são substituídos
// por marcadores: a análise não perde nada com isso e o dado pessoal não sai
// daqui.
public static partial class TranscriptBuilder
{
    private const string NamePlaceholder = "[CLIENTE]";
    private const string PhonePlaceholder = "[TELEFONE]";

    // O prompt inteiro é em português: número com ponto decimal no meio de um
    // texto em pt-BR é convite a leitura errada.
    private static readonly System.Globalization.CultureInfo Ptbr =
        System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    public static string Build(
        IReadOnlyList<TranscriptMessage> messages,
        string? contactName,
        string? contactPhone,
        TimeZoneInfo timeZone,
        bool startedByContact,
        double silenceBusinessHours,
        int maxChars = 12_000)
    {
        var header = new StringBuilder();
        header.Append("Conversa iniciada pel")
            .Append(startedByContact ? "o cliente" : "o vendedor")
            .Append(". Silêncio desde a última mensagem: ")
            .Append(silenceBusinessHours.ToString("0.#", Ptbr))
            .AppendLine(" horas úteis.")
            .AppendLine();

        var lines = messages
            .Select(m => Line(m, contactName, contactPhone, timeZone))
            .ToList();

        return header.ToString() + Fit(lines, maxChars - header.Length);
    }

    private static string Line(TranscriptMessage message, string? name, string? phone, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(message.TimestampUtc, DateTimeKind.Utc), timeZone);
        var who = message.Direction == MessageDirection.Inbound ? "Cliente" : "Vendedor";
        var body = string.IsNullOrWhiteSpace(message.Text)
            ? MediaLabel(message.Type, message.DurationSeconds, message.AudioIndex)
            : Mask(message.Text, name, phone);

        return $"{who} ({local:dd/MM HH:mm}): {body}";
    }

    // Conversa longa é cortada no meio: o começo dá o contexto e o fim é onde
    // mora o desfecho. Tirar o fim seria justamente cegar a análise.
    private static string Fit(List<string> lines, int maxChars)
    {
        var joined = string.Join('\n', lines);
        if (joined.Length <= maxChars || lines.Count < 4)
            return joined;

        var head = new List<string>();
        var tail = new List<string>();
        var budget = maxChars - 40;
        var headBudget = budget / 3;

        var used = 0;
        foreach (var line in lines)
        {
            if (used + line.Length > headBudget)
                break;

            head.Add(line);
            used += line.Length + 1;
        }

        for (var i = lines.Count - 1; i >= head.Count; i--)
        {
            if (used + lines[i].Length > budget)
                break;

            tail.Insert(0, lines[i]);
            used += lines[i].Length + 1;
        }

        var omitted = lines.Count - head.Count - tail.Count;
        return string.Join('\n', head) + $"\n[... {omitted} mensagens omitidas ...]\n" + string.Join('\n', tail);
    }

    public static string Mask(string text, string? name, string? phone)
    {
        var masked = text;

        if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 3)
            masked = Regex.Replace(masked, Regex.Escape(name.Trim()), NamePlaceholder, RegexOptions.IgnoreCase);

        foreach (var variant in PhoneVariants(phone))
            masked = masked.Replace(variant, PhonePlaceholder, StringComparison.Ordinal);

        // Qualquer outro número longo o bastante para ser telefone também sai —
        // o cliente costuma mandar o próprio contato dentro da conversa.
        return PhoneLike().Replace(masked, match =>
            match.Value.Count(char.IsDigit) >= 10 ? PhonePlaceholder : match.Value);
    }

    private static IEnumerable<string> PhoneVariants(string? phone)
    {
        var digits = new string([.. (phone ?? string.Empty).Where(char.IsDigit)]);
        if (digits.Length < 8)
            yield break;

        yield return digits;
        if (digits.Length > 8)
            yield return digits[^8..];
    }

    // A duração vai junto: um áudio de 3 segundos e um de 4 minutos dizem coisas
    // muito diferentes, e sem isso o modelo trata os dois como o mesmo evento.
    // Áudio anexado ganha número: é o que liga o blob enviado ao seu lugar na
    // conversa ("[áudio 2 de 45s]" = o segundo anexo).
    public static string MediaLabel(string type, int? durationSeconds = null, int? audioIndex = null)
    {
        var label = BaseMediaLabel(type);
        if (label is not ("[áudio]" or "[vídeo]"))
            return label;

        var inner = label[1..^1];
        if (audioIndex is > 0)
            inner += $" {audioIndex}";
        if (durationSeconds is > 0)
            inner += $" de {durationSeconds}s";

        return $"[{inner}]";
    }

    private static string BaseMediaLabel(string type) => type switch
    {
        "imageMessage" => "[imagem]",
        "audioMessage" => "[áudio]",
        "pttMessage" => "[áudio]",
        "videoMessage" => "[vídeo]",
        "documentMessage" => "[documento]",
        "stickerMessage" => "[figurinha]",
        "locationMessage" => "[localização]",
        "contactMessage" => "[contato]",
        _ => "[mídia]",
    };

    // O parêntese do DDD entra na captura: sem ele sobrava um "(" órfão no texto.
    [GeneratedRegex(@"\+?\(?\d[\d\s().\-]{8,}\d")]
    private static partial Regex PhoneLike();
}
