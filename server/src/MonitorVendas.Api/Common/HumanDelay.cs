namespace MonitorVendas.Api.Common;

// Tempo de "digitando…" proporcional ao texto, com ruído. A Meta cita
// nominalmente a conta que envia sem disparar o indicador de digitação como
// sinal de abuso — e um delay constante seria tão robótico quanto nenhum.
public static class HumanDelay
{
    // ~30ms por caractere ≈ 45 palavras por minuto, digitação comum no celular.
    public const double MsPerChar = 30;

    // "ok" instantâneo é robô; 105s digitando uma lista inteira também.
    public const int MinMs = 1200;
    public const int MaxMs = 15000;

    private const double ThinkingPauseChance = 0.08;

    public static int ForText(int textLength, IRandomSource random)
    {
        // Fator ~ N(1; 0,25), truncado: digitador bem rápido ou bem lento ainda
        // é humano; velocidade negativa não é.
        var factor = Math.Clamp(random.NextGaussian(1.0, 0.25), 0.4, 2.0);
        var ms = textLength * MsPerChar * factor;

        // De vez em quando a pessoa para pra pensar antes de mandar.
        if (random.NextDouble() < ThinkingPauseChance)
            ms += 800 + random.NextDouble() * 2700;

        return (int)Math.Clamp(ms, MinMs, MaxMs);
    }
}
