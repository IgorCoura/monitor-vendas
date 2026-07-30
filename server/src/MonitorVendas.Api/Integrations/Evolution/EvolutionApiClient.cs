using System.Text.Json;

namespace MonitorVendas.Api.Integrations.Evolution;

public sealed class EvolutionApiClient(HttpClient http)
{
    public async Task SendTextAsync(string instanceName, string number, string text, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            $"message/sendText/{instanceName}",
            new { number, text },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task CreateInstanceAsync(string instanceName, string phone, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            "instance/create",
            new { instanceName, number = phone, qrcode = true, integration = "WHATSAPP-BAILEYS" },
            cancellationToken);

        response.EnsureSuccessStatusCode();
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

    public sealed record FoundMessage(string RawJson, string? KeyId, DateTime? Timestamp);
}
