using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MonitorVendas.Api.Integrations.Ai.Gemini;

public sealed partial class GeminiProvider(HttpClient http, IOptions<AiOptions> options, ILogger<GeminiProvider> logger) : IAiProvider
{
    public string Name => "gemini";

    public async Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var settings = options.Value;
        var model = settings.Model;
        var body = BuildBody(request, settings);
        var waited = TimeSpan.Zero;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await http.PostAsJsonAsync($"models/{model}:generateContent", body, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);

                if (IsTransient(response.StatusCode) && attempt < Math.Max(1, settings.MaxAttempts))
                {
                    // O 429 informa quanto esperar (56s no free tier). Ignorar isso
                    // e voltar em 2s só queima as tentativas sem sair do lugar.
                    var wait = RetryAfter(response, payload, settings) ?? BackoffFor(attempt, settings);

                    // Mas a espera somada tem teto: quem chama tem um prazo a cumprir.
                    if (waited + wait <= TimeSpan.FromSeconds(settings.MaxTotalRetryWaitSeconds))
                    {
                        logger.LogWarning("Gemini devolveu {Status}; nova tentativa ({Attempt}) em {Seconds}s.",
                            (int)response.StatusCode, attempt, wait.TotalSeconds);
                        waited += wait;
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    logger.LogWarning("Gemini devolveu {Status} e a espera passaria de {Total}s; desistindo.",
                        (int)response.StatusCode, settings.MaxTotalRetryWaitSeconds);
                }

                if (!response.IsSuccessStatusCode)
                    throw new AiProviderException(
                        $"Gemini respondeu {(int)response.StatusCode}: {ErrorMessage(payload)}",
                        mayHaveBeenCharged: false);

                return Parse(payload, model);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Estourou o timeout do cliente, não o cancelamento do job: o
                // provedor pode ter gerado (e cobrado) do outro lado.
                throw new AiProviderException("Tempo esgotado esperando o Gemini.", mayHaveBeenCharged: true, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < Math.Max(1, settings.MaxAttempts))
                {
                    logger.LogWarning(ex, "Falha de rede ao chamar o Gemini; tentativa {Attempt}.", attempt);
                    await Task.Delay(BackoffFor(attempt, settings), ct);
                    continue;
                }

                throw new AiProviderException("Não foi possível falar com o Gemini.", mayHaveBeenCharged: false, ex);
            }
        }
    }

    private static JsonObject BuildBody(AiRequest request, AiOptions settings)
    {
        var generationConfig = new JsonObject
        {
            ["temperature"] = settings.Temperature,
            ["maxOutputTokens"] = request.MaxOutputTokens ?? settings.MaxOutputTokens,
        };

        if (request.ResponseJsonSchema is { Length: > 0 } schema)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseSchema"] = JsonNode.Parse(schema);
        }

        if (settings.ThinkingBudgetTokens >= 0)
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = settings.ThinkingBudgetTokens };

        var parts = new JsonArray(new JsonObject { ["text"] = request.UserPrompt });

        // Mídia vai como inline_data na mesma chamada: o modelo ouve o áudio no
        // contexto da conversa, em vez de receber uma transcrição solta.
        foreach (var attachment in request.Attachments ?? [])
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = attachment.MimeType,
                    ["data"] = attachment.Base64Data,
                },
            });
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = parts,
            }),
            ["generationConfig"] = generationConfig,
        };
    }

    private static AiCompletion Parse(string payload, string model)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        // O raciocínio sai do mesmo teto da saída: estourado, o JSON vem cortado
        // no meio e falharia no schema com uma mensagem que não ajuda ninguém.
        var input = 0;
        var output = 0;
        var audio = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            input = ReadInt(usage, "promptTokenCount");
            // O raciocínio vem separado mas é cobrado como saída — e costuma ser a
            // maior parte da conta numa análise curta.
            output = ReadInt(usage, "candidatesTokenCount") + ReadInt(usage, "thoughtsTokenCount");
            audio = AudioTokens(usage);
        }

        var consumed = new AiUsageTokens(model, input, output, audio);

        if (FinishReason(root) is "MAX_TOKENS")
            throw new AiProviderException(
                "O Gemini atingiu o limite de tokens de saída antes de terminar o JSON — aumente Ai:MaxOutputTokens.",
                mayHaveBeenCharged: true)
            { Usage = consumed };

        var text = ExtractText(root);
        if (text is null)
            throw new AiProviderException($"Resposta do Gemini sem texto: {Truncate(payload)}", mayHaveBeenCharged: true)
            { Usage = consumed };

        return new AiCompletion(text, model, input, output, audio);
    }

    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        }

        return null;
    }

    // `promptTokensDetails` quebra a entrada por modalidade
    // ([{ "modality": "AUDIO", "tokenCount": 960 }]). Sem essa separação, áudio
    // seria cobrado ao preço do texto.
    private static int AudioTokens(JsonElement usage)
    {
        if (!usage.TryGetProperty("promptTokensDetails", out var details) || details.ValueKind != JsonValueKind.Array)
            return 0;

        var total = 0;
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.TryGetProperty("modality", out var modality) &&
                modality.ValueKind == JsonValueKind.String &&
                string.Equals(modality.GetString(), "AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                total += ReadInt(detail, "tokenCount");
            }
        }

        return total;
    }

    private static string? FinishReason(JsonElement root) =>
        root.TryGetProperty("candidates", out var candidates) &&
        candidates.ValueKind == JsonValueKind.Array &&
        candidates.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } candidate &&
        candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.InternalServerError or HttpStatusCode.GatewayTimeout;

    private static TimeSpan BackoffFor(int attempt, AiOptions settings) =>
        TimeSpan.FromSeconds(Math.Max(0, settings.RetryBackoffSeconds) * attempt);

    // O tempo de espera vem no header padrão ou no `retryDelay` do corpo do erro.
    private static TimeSpan? RetryAfter(HttpResponseMessage response, string payload, AiOptions settings)
    {
        double? seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;

        if (seconds is null)
        {
            var match = RetryDelay().Match(payload);
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var parsed))
                seconds = parsed;
        }

        if (seconds is null)
            return null;

        // O teto existe para a exportação não ficar horas presa num limite diário.
        return seconds > settings.MaxRetryDelaySeconds
            ? null
            : TimeSpan.FromSeconds(Math.Ceiling(seconds.Value) + 1);
    }

    [GeneratedRegex(@"""retryDelay""\s*:\s*""([0-9.]+)s""")]
    private static partial Regex RetryDelay();

    private static string Truncate(string value) => value.Length > 300 ? value[..300] : value;

    // A mensagem vai parar numa célula da planilha: despejar o JSON de erro inteiro
    // ali é ilegível para quem abre o arquivo.
    private static string ErrorMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                return Truncate(message.GetString()!.Split('\n')[0].Trim());
        }
        catch (JsonException)
        {
        }

        return Truncate(payload);
    }
}
