using System.Text.Json;

namespace MonitorVendas.Api.Features.Conversations;

// Helpers de parsing dos payloads da Evolution (webhook real ou sintetizado pela reconciliação).
public static class WebhookPayload
{
    public static JsonElement? GetData(string payload, JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var data) ? data : null;

    public static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    public static DateTime? GetUnixTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var ts))
            return null;

        if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        if (ts.ValueKind == JsonValueKind.String && long.TryParse(ts.GetString(), out var parsed))
            return DateTimeOffset.FromUnixTimeSeconds(parsed).UtcDateTime;

        return null;
    }

    public static DateTime GetEnvelopeTime(JsonDocument doc, DateTime fallback)
    {
        if (doc.RootElement.TryGetProperty("date_time", out var dt) &&
            dt.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(dt.GetString(), out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return fallback;
    }

    // A IDENTIDADE DO CONTATO, num lugar só.
    //
    // O WhatsApp passou a endereçar por LID (`42309773226234@lid`, com
    // `addressingMode: "lid"`), e nesse modo o `remoteJid` não contém telefone
    // nenhum: guardar o contato por ele cria um segundo cadastro da mesma pessoa,
    // sem número, e a exportação — que existe para produzir
    // "Nome - 5511999998888" — sai sem o número. O `remoteJidAlt` traz o JID de
    // telefone quando o modo é LID, e é ele que vale.
    //
    // Toda leitura de JID passa por aqui de propósito: quatro handlers com quatro
    // regras próprias é garantia de que um dia divergem, e foi assim que o
    // aquecimento vazou para as métricas.
    public static string? ResolveJid(JsonElement element) =>
        GetString(element, "remoteJidAlt") ?? GetString(element, "remoteJid");

    public static bool IsLid(string jid) => jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);

    public static bool IsGroupOrBroadcast(string remoteJid) =>
        remoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
        remoteJid.Contains("@broadcast", StringComparison.OrdinalIgnoreCase);

    // Duração do áudio/vídeo. Sem ela a transcrição diria só "[áudio]", e um de 3
    // segundos e um de 4 minutos contam histórias diferentes sobre a conversa.
    public static int? ExtractDurationSeconds(JsonElement data)
    {
        if (!data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var media in new[] { "audioMessage", "videoMessage", "pttMessage" })
        {
            if (!message.TryGetProperty(media, out var element) || element.ValueKind != JsonValueKind.Object)
                continue;

            if (!element.TryGetProperty("seconds", out var seconds))
                continue;

            if (seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var value))
                return value;

            if (seconds.ValueKind == JsonValueKind.String && int.TryParse(seconds.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    // Extrai o texto conforme o tipo: texto puro, texto estendido ou legenda de mídia.
    public static string? ExtractText(JsonElement data)
    {
        if (!data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;

        if (GetString(message, "conversation") is { } conversation)
            return conversation;

        foreach (var container in new[] { "extendedTextMessage" })
            if (message.TryGetProperty(container, out var ext) && GetString(ext, "text") is { } text)
                return text;

        foreach (var media in new[] { "imageMessage", "videoMessage", "documentMessage" })
            if (message.TryGetProperty(media, out var element) && GetString(element, "caption") is { } caption)
                return caption;

        return null;
    }
}
