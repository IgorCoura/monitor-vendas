using System.Globalization;
using System.Text;

namespace MonitorVendas.Api.Features.Outcomes;

// Catálogo de tipos de desfecho. Novos tipos (cliente pensando, esperando
// pagamento, ...) são criados pela tela — não exigem migração nem código, porque
// o agregado guarda desfecho por tipo em tabela filha.
public class ConversationOutcomeType
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}

public static class OutcomeTypeCodes
{
    public const string Sale = "sale";
    public const string Lost = "lost";
}

// Etiqueta aceita para um tipo. `Term` é o texto como o usuário digitou (exibição);
// `NormalizedKey` é o que realmente compara.
public class OutcomeLabelTerm
{
    public Guid Id { get; set; }
    public string OutcomeTypeCode { get; set; } = string.Empty;
    public string Term { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// Etiquetas de WhatsApp quase sempre trazem emoji e variam em caixa/acento:
// "Fechado ✅", "FECHADO" e "fechado" precisam ser a mesma chave.
public static class LabelNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;

        foreach (var ch in decomposed)
        {
            // Remove acentos (marcas de combinação) e qualquer símbolo/emoji.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
