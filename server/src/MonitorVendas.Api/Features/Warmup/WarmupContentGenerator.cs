using System.Text.Json;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Warmup;

public interface IWarmupContentGenerator
{
    Task<GeneratedConversation?> GenerateAsync(
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

    public async Task<GeneratedConversation?> GenerateAsync(
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
            return null;
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
            return null;
        }

        await budget.SettleAsync(
            reservation.Id, completion.Model, completion.InputTokens, completion.OutputTokens, ct: ct);

        var parsed = Parse(theme, completion.Text);
        if (parsed is null)
        {
            logger.LogWarning("Resposta de aquecimento ilegível; conversa descartada.");
            return null;
        }

        // Descartar é barato; mandar um link não é.
        if (!WarmupContentValidator.IsAcceptable(parsed, out var reason))
        {
            logger.LogWarning("Conversa de aquecimento descartada ({Reason}).", reason);
            return null;
        }

        return parsed;
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
