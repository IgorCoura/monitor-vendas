using System.Text.Json;
using MonitorVendas.Api.Common;

namespace MonitorVendas.Api.Integrations.Evolution;

public sealed class EvolutionApiClient(HttpClient http, IRandomSource random)
{
    // Folga de rede além do "digitando". O delay é SÍNCRONO na Evolution
    // (v2.3.7, medido: delay 8000 → resposta em 9,07s), então o teto do envio é
    // delay + esta folga — sem ele, um envio travado seguraria a fila pelos
    // 100s do default do HttpClient.
    private static readonly TimeSpan SendNetworkBudget = TimeSpan.FromSeconds(15);

    // Devolve o id da mensagem criada (o webhook dela volta como MESSAGES_UPSERT e
    // seria contado como mensagem enviada pelo vendedor dono do número) e o delay
    // de digitação usado, para o chamador descontar do intervalo entre mensagens.
    // O `presence: composing` vive AQUI, não no chamador: a Meta cita a conta que
    // envia sem disparar o indicador de digitação como sinal de abuso, e nenhum
    // envio futuro pode esquecer disso.
    public async Task<SendResult> SendTextAsync(string instanceName, string number, string text, CancellationToken cancellationToken = default)
    {
        var delayMs = HumanDelay.ForText(text.Length, random);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(delayMs) + SendNetworkBudget);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"message/sendText/{instanceName}",
                new { number, text, delay = delayMs, presence = "composing" },
                cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout nosso, não cancelamento de quem chamou: vira falha de envio
            // comum, que o chamador já sabe contar e retomar.
            throw new HttpRequestException($"O envio por {instanceName} não respondeu dentro do tempo esperado.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // 463 = a conta chegou ao limite de contato frio. Não é falha
            // transitória para tentar de novo: é o WhatsApp mandando parar.
            if (LooksRestricted(errorBody))
                return new SendResult(null, delayMs, (int)response.StatusCode, Restricted: true);

            // O corpo cru vai na exceção de propósito: o formato dos erros da
            // Evolution não é documentado, e é deste log que sai o parser preciso.
            var detail = errorBody.Length > 500 ? errorBody[..500] : errorBody;
            throw new HttpRequestException($"Evolution respondeu {(int)response.StatusCode} ao envio: {detail}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var keyId = doc.RootElement.TryGetProperty("key", out var key) ? GetString(key, "id") : null;
        return new SendResult(keyId, delayMs);
    }

    // "463" solto não basta: o corpo de erro pode ecoar o texto enviado, e uma
    // lista de contatos tem telefones com "463" no meio. Só vale como restrição
    // quando aparece como código (`"code": 463`) ou pelos nomes do Baileys.
    private static readonly System.Text.RegularExpressions.Regex RestrictedCode = new(
        """["'](?:status|statusCode|code|reason)["']\s*:\s*["']?463""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksRestricted(string body) =>
        body.Contains("reachout", StringComparison.OrdinalIgnoreCase)
        || body.Contains("timelock", StringComparison.OrdinalIgnoreCase)
        || RestrictedCode.IsMatch(body);

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
    // Devolve o QR da criação — e é AQUI que sai o código de pareamento: pedi-lo
    // depois, em `instance/connect/{name}?number=`, devolve `pairingCode: null`
    // quando a instância nasceu sem número (confirmado contra a v2.3.7).
    public async Task<QrCode?> CreateInstanceAsync(string instanceName, string? phone = null, CancellationToken cancellationToken = default)
    {
        object body = string.IsNullOrWhiteSpace(phone)
            ? new { instanceName, qrcode = true, integration = "WHATSAPP-BAILEYS" }
            : new { instanceName, number = phone, qrcode = true, integration = "WHATSAPP-BAILEYS" };

        var response = await http.PostAsJsonAsync("instance/create", body, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("qrcode", out var qr) || qr.ValueKind != JsonValueKind.Object)
            return null;

        return new QrCode(GetString(qr, "code"), GetString(qr, "base64"), GetString(qr, "pairingCode"));
    }

    // Derruba e sobe o socket SEM desvincular o aparelho: é o remédio para
    // instância travada (`connecting` que não sai do lugar, parou de receber).
    // Não confundir com o logout, que exige QR novo para o número voltar.
    public async Task<bool> RestartAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            // POST, não PUT: o PUT responde 404 na v2.3.7.
            var response = await http.PostAsync($"instance/restart/{instanceName}", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
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

    // Com `phone`, a Evolution devolve também o **código de pareamento** — é a
    // saída de quem abre o painel no próprio celular e não tem uma segunda câmera
    // para ler o QR da tela. O número aqui serve só para o WhatsApp saber a quem
    // mandar o código; quem manda no cadastro continua sendo o `wuid` da conexão.
    public async Task<QrCode> ConnectAsync(string instanceName, string? phone = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(phone)
            ? $"instance/connect/{instanceName}"
            : $"instance/connect/{instanceName}?number={Uri.EscapeDataString(phone)}";

        var response = await http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new QrCode(GetString(root, "code"), GetString(root, "base64"), GetString(root, "pairingCode"));
    }

    // O 404 aqui é RESPOSTA, não falha: a instância não existe mais na Evolution.
    // Tratá-lo como "fora do ar" (exceção) foi o que deixava número fantasma
    // aparecendo conectado para sempre — quem chama precisa distinguir os dois.
    public async Task<InstanceState> GetConnectionStateAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"instance/connectionState/{instanceName}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new InstanceState(null, Missing: true);

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("instance", out var instance))
            return new InstanceState(GetString(instance, "state"), Missing: false);

        return new InstanceState(null, Missing: false);
    }

    // Lista o que existe do lado da Evolution — é a metade dela da conciliação:
    // instância órfã (sem número nem sessão de pareamento viva) é lixo a varrer.
    public async Task<IReadOnlyList<InstanceInfo>> FetchInstancesAsync(CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync("instance/fetchInstances", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<InstanceInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (GetString(item, "name") is not { Length: > 0 } name)
                continue;

            DateTime? createdAt = DateTime.TryParse(
                GetString(item, "createdAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

            result.Add(new InstanceInfo(name, createdAt));
        }

        return result;
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

    public sealed record SendResult(string? KeyId, int DelayMs, int? ErrorCode = null, bool Restricted = false);

    public sealed record InstanceState(string? State, bool Missing);

    public sealed record InstanceInfo(string Name, DateTime? CreatedAt);

    public sealed record Media(string Base64, string MimeType);

    public sealed record FoundMessage(string RawJson, string? KeyId, DateTime? Timestamp);
}
