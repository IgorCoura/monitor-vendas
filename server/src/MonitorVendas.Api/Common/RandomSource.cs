namespace MonitorVendas.Api.Common;

// Fonte de aleatoriedade injetável: o envio simula ritmo humano com ruído, e o
// teste só é determinístico se o ruído vier de fora.
public interface IRandomSource
{
    // Amostra de uma normal. Usada no fator de velocidade de digitação.
    double NextGaussian(double mean, double stdDev);

    // Uniforme em [0, 1).
    double NextDouble();
}

public sealed class RandomSource : IRandomSource
{
    public double NextGaussian(double mean, double stdDev)
    {
        // Box-Muller; u1 não pode ser 0 porque log(0) explode.
        var u1 = 1.0 - Random.Shared.NextDouble();
        var u2 = Random.Shared.NextDouble();
        var standard = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * standard;
    }

    public double NextDouble() => Random.Shared.NextDouble();
}
