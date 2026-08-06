using System.Text.RegularExpressions;

namespace MonitorVendas.Api.Features.Warmup;

public sealed record GeneratedTurn(bool FromA, string Text);

public sealed record GeneratedConversation(string Theme, IReadOnlyList<GeneratedTurn> Turns);

// O que o modelo NÃO pode produzir. Descartar e gerar de novo custa centavos;
// mandar um link no aquecimento custa o número.
public static partial class WarmupContentValidator
{
    // Sequência longa de dígitos: telefone, CPF, número de matrícula. Preço e
    // link têm padrões próprios.
    [GeneratedRegex(@"\d[\d\s.\-()]{7,}")]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"https?://|www\.|\b[\w.-]+\.(com|br|net|org|io)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"R\$\s*\d|\breais\b|\bdesconto\b|\bpromoç|\bmatricule\b|\bcompre\b|\bclique\b", RegexOptions.IgnoreCase)]
    private static partial Regex Salesy();

    public static bool IsAcceptable(GeneratedConversation conversation, out string? reason)
    {
        foreach (var turn in conversation.Turns)
        {
            var text = turn.Text?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                reason = "turno vazio";
                return false;
            }

            // Mensagem longa demais não é conversa de WhatsApp entre colegas —
            // é texto de anúncio disfarçado.
            if (text.Length > 180)
            {
                reason = "mensagem longa demais";
                return false;
            }

            if (Link().IsMatch(text))
            {
                reason = "contém link";
                return false;
            }

            if (LongDigits().IsMatch(text))
            {
                reason = "contém sequência de dígitos (telefone/documento)";
                return false;
            }

            if (Salesy().IsMatch(text))
            {
                reason = "tem cara de anúncio";
                return false;
            }
        }

        if (conversation.Turns.Count < 2)
        {
            reason = "conversa curta demais";
            return false;
        }

        // Os dois lados precisam falar: monólogo não gera o sinal de conversa de
        // mão dupla, que é o motivo de existir esta feature.
        if (conversation.Turns.All(t => t.FromA) || conversation.Turns.All(t => !t.FromA))
        {
            reason = "só um lado fala";
            return false;
        }

        reason = null;
        return true;
    }
}

// Como cada número escreve. Fixo por número: sem isso, os dois lados de um par
// soariam como a mesma pessoa conversando consigo mesma.
public static class WarmupPersonaDescriptions
{
    public static string Describe(WarmupPersona persona) => persona switch
    {
        WarmupPersona.Seco => "responde curto e direto, quase telegráfico, sem emoji",
        WarmupPersona.Falante => "fala mais, às vezes manda duas ideias na mesma mensagem",
        WarmupPersona.Informal => "escreve tudo em minúsculas, abrevia muito (vc, tb, blz, pq)",
        WarmupPersona.Expressivo => "usa emoji com moderação e pontuação expressiva (!, ...)",
        _ => "escreve de forma comum",
    };
}
