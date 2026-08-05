using System.Text.Json;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Warmup;

// Classificado na ORIGEM, não por regex sobre a mensagem guardada: "acabou o
// saldo em reais" e "acabou a cota do Google" pedem ações opostas de quem opera,
// e a tela precisa dizer qual é qual.
public static class WarmupGenerationError
{
    // Saldo em reais da janela — nosso freio, controlado por config.
    public const string Budget = "budget";

    // Cota do provedor (429). Nenhuma config nossa resolve.
    public const string Quota = "quota";

    // A IA respondeu, mas o texto foi recusado na validação.
    public const string Content = "content";

    public const string Provider = "provider";
}

// O motivo da falha viaja junto: sem ele o agendador não sabe se deve recuar, e
// a tela não tem o que mostrar além de silêncio.
public sealed record GenerationOutcome(GeneratedConversation? Conversation, string? Error, string? Kind = null)
{
    public static GenerationOutcome Ok(GeneratedConversation conversation) => new(conversation, null);

    public static GenerationOutcome Fail(string kind, string error) => new(null, error, kind);
}

public interface IWarmupContentGenerator
{
    Task<GenerationOutcome> GenerateAsync(
        WarmupPersona personaA, WarmupPersona personaB, int turns, CancellationToken ct);
}

// Gera a conversa inteira numa chamada só. Conversa inteira, e não mensagens
// soltas, porque resposta que não casa com a pergunta é um sinal pior que
// repetição — e porque o modelo escreve melhor um diálogo do que uma frase sem
// contexto.
public sealed class WarmupContentGenerator(
    IAiProvider provider,
    AiBudget budget,
    AiCostCalculator costs,
    IOptions<AiOptions> aiOptions,
    IOptions<WarmupOptions> options,
    IRandomSource random,
    ILogger<WarmupContentGenerator> logger) : IWarmupContentGenerator
{
    private const string Purpose = "warmup";

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "turnos": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "de": { "type": "string", "enum": ["A", "B"] },
                  "texto": { "type": "string" }
                },
                "required": ["de", "texto"]
              }
            }
          },
          "required": ["turnos"]
        }
        """;

    public async Task<GenerationOutcome> GenerateAsync(
        WarmupPersona personaA, WarmupPersona personaB, int turns, CancellationToken ct)
    {
        var settings = options.Value;
        var theme = settings.Themes[(int)(random.NextDouble() * settings.Themes.Count) % settings.Themes.Count];

        var system = """
            Você escreve diálogos curtos de WhatsApp entre dois COLEGAS DE TRABALHO de uma
            empresa que vende cursos de pós-graduação e licenciatura.

            Regras absolutas:
            - Português brasileiro coloquial, como gente digita no celular.
            - Mensagens CURTAS: de 3 a 12 palavras. Nada de parágrafo.
            - NUNCA inclua link, endereço de site, telefone, CPF, valor em reais,
              desconto, promoção ou qualquer coisa com cara de anúncio.
            - NUNCA use nome de aluno real; se precisar citar alguém, use "o aluno",
              "a moça do polo", "o coordenador".
            - É conversa entre colegas, não atendimento a cliente.
            - Os dois lados falam; alternam de forma natural, não perfeitamente.
            - Pode ter abreviação, minúscula e um erro de digitação ocasional.
            """;

        var user = $"""
            Escreva {turns} mensagens no total, alternando entre A e B.

            Assunto: {theme}.

            Como cada um escreve:
            - A: {WarmupPersonaDescriptions.Describe(personaA)}
            - B: {WarmupPersonaDescriptions.Describe(personaB)}

            Devolva apenas o JSON no schema pedido.
            """;

        var estimate = costs.WithMargin(
            costs.RawCostBrl(aiOptions.Value.Model, inputTokens: 400, outputTokens: 60 * turns),
            20);

        var reservation = await budget.TryReserveAsync(Purpose, aiOptions.Value.Model, estimate, ct);
        if (reservation is null)
        {
            // Sem saldo o aquecimento PARA. Não há queda para banco de frases:
            // com milhares de mensagens por mês, repetição literal é o caminho
            // mais curto para o padrão ser pego.
            logger.LogWarning("Aquecimento sem saldo de IA: nenhuma conversa gerada.");
            return GenerationOutcome.Fail(
                WarmupGenerationError.Budget,
                "Sem saldo de IA na janela atual. O aquecimento divide o mesmo saldo com a análise de conversas.");
        }

        AiCompletion completion;
        try
        {
            completion = await provider.CompleteAsync(
                new AiRequest(system, user, Schema, MaxOutputTokens: 200 + 80 * turns), ct);
        }
        catch (AiProviderException ex)
        {
            if (ex.Usage is { } usage)
                await budget.SettleAsync(reservation.Id, usage.Model, usage.InputTokens, usage.OutputTokens, usage.InputAudioTokens, ct);
            else if (!ex.MayHaveBeenCharged)
                await budget.ReleaseAsync(reservation.Id, ct);

            logger.LogWarning(ex, "Falha ao gerar conversa de aquecimento.");

            // 429 do provedor é cota estourada: nenhuma config nossa resolve, e
            // dizer "sem saldo" mandaria quem opera mexer no lugar errado.
            var quota = ex.Message.Contains("429")
                || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);

            return GenerationOutcome.Fail(
                quota ? WarmupGenerationError.Quota : WarmupGenerationError.Provider,
                ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
        }

        await budget.SettleAsync(
            reservation.Id, completion.Model, completion.InputTokens, completion.OutputTokens, ct: ct);

        var parsed = Parse(theme, completion.Text);
        if (parsed is null)
        {
            logger.LogWarning("Resposta de aquecimento ilegível; conversa descartada.");
            return GenerationOutcome.Fail(WarmupGenerationError.Content, "A IA devolveu uma resposta ilegível.");
        }

        // Descartar é barato; mandar um link não é.
        if (!WarmupContentValidator.IsAcceptable(parsed, out var reason))
        {
            logger.LogWarning("Conversa de aquecimento descartada ({Reason}).", reason);
            return GenerationOutcome.Fail(
                WarmupGenerationError.Content, $"Conversa gerada foi recusada na validação: {reason}.");
        }

        return GenerationOutcome.Ok(parsed);
    }

    private static GeneratedConversation? Parse(string theme, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("turnos", out var turns) || turns.ValueKind != JsonValueKind.Array)
                return null;

            var parsed = new List<GeneratedTurn>();
            foreach (var turn in turns.EnumerateArray())
            {
                var from = turn.TryGetProperty("de", out var de) ? de.GetString() : null;
                var text = turn.TryGetProperty("texto", out var t) ? t.GetString() : null;
                if (from is null || string.IsNullOrWhiteSpace(text))
                    continue;

                parsed.Add(new GeneratedTurn(from.Trim().Equals("A", StringComparison.OrdinalIgnoreCase), text.Trim()));
            }

            return parsed.Count == 0 ? null : new GeneratedConversation(theme, parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
