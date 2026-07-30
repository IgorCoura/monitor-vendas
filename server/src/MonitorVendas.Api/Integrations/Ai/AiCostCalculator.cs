using Microsoft.Extensions.Options;

namespace MonitorVendas.Api.Integrations.Ai;

// Converte tokens em reais. Puro e sem rede: o custo precisa ser calculável
// antes de existir qualquer resposta do provedor.
public sealed class AiCostCalculator(IOptions<AiOptions> options)
{
    private const int Precision = 6;

    // Custo real, sem margem. Modelo sem preço cadastrado é erro alto e claro:
    // devolver zero aqui significaria gasto sem teto.
    public decimal RawCostBrl(string model, int inputTokens, int outputTokens)
    {
        var settings = options.Value;
        if (!settings.Pricing.TryGetValue(model, out var pricing))
            throw new InvalidOperationException(
                $"Modelo '{model}' não tem preço em Ai:Pricing — sem isso o gasto não pode ser controlado.");

        var usd = (inputTokens * pricing.InputUsdPerMillion + outputTokens * pricing.OutputUsdPerMillion) / 1_000_000m;
        return Math.Round(usd * settings.UsdBrlRate, Precision, MidpointRounding.AwayFromZero);
    }

    public decimal WithMargin(decimal brl, decimal marginPercent) =>
        Math.Round(brl * (1 + marginPercent / 100m), Precision, MidpointRounding.AwayFromZero);

    public int EstimateInputTokens(string prompt)
    {
        var settings = options.Value;
        var charsPerToken = settings.CharsPerToken <= 0 ? 4 : settings.CharsPerToken;
        return (int)Math.Ceiling(prompt.Length / charsPerToken * Math.Max(1, settings.EstimateSafetyFactor));
    }

    // Teto: entrada estimada por caracteres + o máximo de saída que o pedido
    // permite. Superestimar é o lado seguro — bloqueia antes de furar o saldo.
    public decimal EstimateBrl(string model, string prompt, int maxOutputTokens, decimal marginPercent) =>
        WithMargin(RawCostBrl(model, EstimateInputTokens(prompt), maxOutputTokens), marginPercent);
}
