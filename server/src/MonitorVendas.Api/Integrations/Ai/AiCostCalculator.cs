using Microsoft.Extensions.Options;

namespace MonitorVendas.Api.Integrations.Ai;

// Converte tokens em reais. Puro e sem rede: o custo precisa ser calculável
// antes de existir qualquer resposta do provedor.
public sealed class AiCostCalculator(IOptions<AiOptions> options)
{
    private const int Precision = 6;

    // Custo real, sem margem. Modelo sem preço cadastrado é erro alto e claro:
    // devolver zero aqui significaria gasto sem teto.
    //
    // `inputAudioTokens` é parte de `inputTokens` e sai da tarifa de áudio; o que
    // sobra é texto.
    public decimal RawCostBrl(string model, int inputTokens, int outputTokens, int inputAudioTokens = 0)
    {
        var settings = options.Value;
        if (!settings.Pricing.TryGetValue(model, out var pricing))
            throw new InvalidOperationException(
                $"Modelo '{model}' não tem preço em Ai:Pricing — sem isso o gasto não pode ser controlado.");

        var audioTokens = Math.Clamp(inputAudioTokens, 0, inputTokens);
        if (audioTokens > 0 && pricing.AudioInputUsdPerMillion is null)
            throw new InvalidOperationException(
                $"Modelo '{model}' recebeu áudio mas não tem AudioInputUsdPerMillion em Ai:Pricing — " +
                "cobrar áudio ao preço de texto subfaturaria o saldo.");

        var textTokens = inputTokens - audioTokens;
        var usd = (textTokens * pricing.InputUsdPerMillion
            + audioTokens * (pricing.AudioInputUsdPerMillion ?? 0m)
            + outputTokens * pricing.OutputUsdPerMillion) / 1_000_000m;

        return Math.Round(usd * settings.UsdBrlRate, Precision, MidpointRounding.AwayFromZero);
    }

    public int EstimateAudioTokens(double seconds) =>
        (int)Math.Ceiling(Math.Max(0, seconds) * Math.Max(1, options.Value.AudioTokensPerSecond));

    public decimal WithMargin(decimal brl, decimal marginPercent) =>
        Math.Round(brl * (1 + marginPercent / 100m), Precision, MidpointRounding.AwayFromZero);

    public int EstimateInputTokens(string prompt)
    {
        var settings = options.Value;
        var charsPerToken = settings.CharsPerToken <= 0 ? 4 : settings.CharsPerToken;
        return (int)Math.Ceiling(prompt.Length / charsPerToken * Math.Max(1, settings.EstimateSafetyFactor));
    }

    // Teto: entrada estimada por caracteres, mais os tokens do áudio anexado, mais
    // o máximo de saída que o pedido permite. Superestimar é o lado seguro —
    // bloqueia antes de furar o saldo.
    public decimal EstimateBrl(
        string model,
        string prompt,
        int maxOutputTokens,
        decimal marginPercent,
        double audioSeconds = 0)
    {
        var audioTokens = EstimateAudioTokens(audioSeconds);
        var input = EstimateInputTokens(prompt) + audioTokens;

        return WithMargin(RawCostBrl(model, input, maxOutputTokens, audioTokens), marginPercent);
    }
}
