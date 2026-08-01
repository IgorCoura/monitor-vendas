using System.Text.Json;

namespace MonitorVendas.Api.Integrations.Evolution;

public sealed class EvolutionApiClient(HttpClient http)
{
    // Devolve o id da mensagem criada: o webhook dela volta como MESSAGES_UPSERT e
    // seria contado como mensagem enviada pelo vendedor dono do número.
    public async Task<string?> SendTextAsync(string instanceName, string number, string text, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            $"message/sendText/{instanceName}",
            new { number, text },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.TryGetProperty("key", out var key) ? GetString(key, "id") : null;
    }

    // Baixa a mídia de uma mensagem (áudio, imagem) em base64. Devolve null em vez
    // de estourar: mídia expirada ou instância fora do ar não pode derrubar a
    // análise da conversa, que segue valendo pelo texto.
    public async Task<Media?> GetMediaAsync(string instanceName, string waMessageId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"chat/getBase64FromMediaMessage/{instanceName}",
                new { message = new { key = new { id = waMessageId } }, convertToMp4 = false },
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;

        var base64 = GetString(root, "base64");
        if (base64 is null)
            return null;

        return new Media(base64, GetString(root, "mimetype") ?? "application/octet-stream");
    }

    // `number` é OMITIDO quando não se sabe o telefone — mandá-lo vazio faz a
    // Evolution recusar com 400 ("number does not match pattern"). É o caso do
    // pareamento por QR, em que o número só aparece depois de conectar.
    public async Task CreateInstanceAsync(string instanceName, string? phone = null, CancellationToken cancellationToken = default)
    {
        object body = string.IsNullOrWhiteSpace(phone)
            ? new { instanceName, qrcode = true, integration = "WHATSAPP-BAILEYS" }
            : new { instanceName, number = phone, qrcode = true, integration = "WHATSAPP-BAILEYS" };

        var response = await http.PostAsJsonAsync("instance/create", body, cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    // Derruba a sessão sem apagar a instância: o número pode voltar depois pelo
    // mesmo registro. Best-effort de propósito — sessão já caída responde erro, e
    // isso não pode impedir quem chamou de registrar a decisão dele.
    public async Task<bool> LogoutAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await http.DeleteAsync($"instance/logout/{instanceName}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // Apaga a instância e, por cascata no banco da Evolution, todo o histórico
    // dela (chats, contatos, mensagens, etiquetas). Só é seguro porque a fonte de
    // verdade é o NOSSO banco: lá a Evolution é transporte, não arquivo.
    public async Task<bool> DeleteInstanceAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await http.DeleteAsync($"instance/delete/{instanceName}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task SetWebhookAsync(string instanceName, string url, IReadOnlyCollection<string> events, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            $"webhook/set/{instanceName}",
            new { webhook = new { enabled = true, url, byEvents = false, base64 = false, events } },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<QrCode> ConnectAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"instance/connect/{instanceName}", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new QrCode(GetString(root, "code"), GetString(root, "base64"), GetString(root, "pairingCode"));
    }

    public async Task<string?> GetConnectionStateAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"instance/connectionState/{instanceName}", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("instance", out var instance))
            return GetString(instance, "state");

        return null;
    }

    public async Task<IReadOnlyList<FoundMessage>> FindMessagesAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync($"chat/findMessages/{instanceName}", new { where = new { } }, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var records = doc.RootElement;

        // A resposta pode vir como array direto ou paginada em messages.records.
        if (records.ValueKind == JsonValueKind.Object && records.TryGetProperty("messages", out var messages))
            records = messages.ValueKind == JsonValueKind.Object && messages.TryGetProperty("records", out var inner)
                ? inner
                : messages;

        var result = new List<FoundMessage>();
        if (records.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var record in records.EnumerateArray())
        {
            string? keyId = null;
            if (record.TryGetProperty("key", out var key))
                keyId = GetString(key, "id");

            DateTime? timestamp = null;
            if (record.TryGetProperty("messageTimestamp", out var ts))
            {
                if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var seconds))
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                else if (ts.ValueKind == JsonValueKind.String && long.TryParse(ts.GetString(), out var parsed))
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(parsed).UtcDateTime;
            }

            result.Add(new FoundMessage(record.GetRawText(), keyId, timestamp));
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public sealed record QrCode(string? Code, string? Base64, string? PairingCode);

    public sealed record Media(string Base64, string MimeType);

    public sealed record FoundMessage(string RawJson, string? KeyId, DateTime? Timestamp);
}
