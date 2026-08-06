using System.Globalization;
using System.Text;

namespace MonitorVendas.Api.Features.Conversations;

// Reconhece o pedido de descadastro numa mensagem do cliente. Puro: a lista de
// termos e a normalização são a regra inteira, e ela precisa ser testável sem
// banco.
public static class OptOutDetector
{
    // Só termos INEQUÍVOCOS. "Não quero" e "para" soltos aparecem em conversa
    // normal de venda ("não quero o azul", "para quando?") e um falso positivo
    // aqui silencia um cliente ativo — erro pior que deixar passar.
    private static readonly string[] Terms =
    [
        "sair", "parar", "pare", "descadastrar", "descadastre", "cancelar inscricao",
        "nao quero mais receber", "nao quero receber", "remover meu numero", "me remova",
        "stop", "unsubscribe",
    ];

    public static bool IsOptOut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Normalize(text);

        // Mensagem curta: o pedido é a mensagem inteira ("SAIR", "Pare por
        // favor"). Num texto longo, "pare" quase sempre é outra coisa.
        if (normalized.Length > 40)
            return Terms.Any(t => t.Contains(' ') && normalized.Contains(t, StringComparison.Ordinal));

        return Terms.Any(t => normalized == t || normalized.Contains(t, StringComparison.Ordinal));
    }

    // Minúsculas, sem acento e sem pontuação — "PARE!" e "Pare" são o mesmo
    // pedido. Mesmo espírito do LabelNormalizer do catálogo de desfechos.
    private static string Normalize(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch) || ch == ' ')
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }

        return builder.ToString().Trim();
    }
}
